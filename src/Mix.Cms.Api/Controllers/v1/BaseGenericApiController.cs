﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Mix.Cms.Hub;
using Mix.Cms.Lib;
using Mix.Cms.Lib.Repositories;
using Mix.Cms.Lib.Services;
using Mix.Cms.Lib.ViewModels;
using Mix.Domain.Core.ViewModels;
using Mix.Domain.Data.Repository;
using Mix.Domain.Data.ViewModels;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using static Mix.Cms.Lib.MixEnums;

namespace Mix.Cms.Api.Controllers.v1
{
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SuperAdmin, Admin")]
    public class BaseGenericApiController<TDbContext, TModel> : Controller
        where TDbContext : DbContext
        where TModel : class
    {
        protected readonly IHubContext<PortalHub> _hubContext;

        protected IMemoryCache _memoryCache;

        /// <summary>
        /// The language
        /// </summary>
        protected string _lang;

        protected bool _forbidden = false;
        protected bool _forbiddenPortal
        {
            get
            {
                var allowedIps = MixService.GetIpConfig<JArray>("AllowedPortalIps") ?? new JArray();
                string remoteIp = Request.HttpContext?.Connection?.RemoteIpAddress?.ToString();
                return _forbidden || (
                    // allow localhost
                    //remoteIp != "::1" &&
                    (allowedIps != null && !allowedIps.Any(t => t.Value<string>() == "*") && !allowedIps.Contains(remoteIp))
                );
            }
        }

        /// <summary>
        /// The domain
        /// </summary>
        protected string _domain;

        /// <summary>
        /// The repo
        /// </summary>
        /// <summary>
        /// Initializes a new instance of the <see cref="BaseApiController"/> class.
        /// </summary>
        public BaseGenericApiController(IMemoryCache memoryCache, IHubContext<PortalHub> hubContext)
        {
            _hubContext = hubContext;
            _memoryCache = memoryCache;
        }


        #region Overrides

        /// <summary>
        /// Called before the action method is invoked.
        /// </summary>
        /// <param name="context">The action executing context.</param>
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            GetLanguage();
            AlertAsync("Executing request", 200);
            if (MixService.GetIpConfig<bool>("IsRetrictIp"))
            {
                var allowedIps = MixService.GetIpConfig<JArray>("AllowedIps") ?? new JArray();
                var exceptIps = MixService.GetIpConfig<JArray>("ExceptIps") ?? new JArray();
                string remoteIp = Request.HttpContext?.Connection?.RemoteIpAddress?.ToString();
                if (
                    // allow localhost
                    //remoteIp != "::1" &&
                    (!allowedIps.Any(t => t.Value<string>() == "*") && !allowedIps.Contains(remoteIp)) ||
                    (exceptIps.Any(t => t.Value<string>() == remoteIp))
                    )
                {
                    _forbidden = true;
                }
            }
            base.OnActionExecuting(context);
        }


        #endregion

        protected async Task<RepositoryResponse<TView>> GetSingleAsync<TView>(string key, Expression<Func<TModel, bool>> predicate = null, TModel model = null)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            var getPage = new RepositoryResponse<Lib.ViewModels.MixPages.ReadMvcViewModel>();
            var cacheKey = $"{typeof(TModel).Name}_details_{_lang}_{key}";

            if (!_memoryCache.TryGetValue<RepositoryResponse<TView>>(cacheKey, out RepositoryResponse<TView> data))
            {

                if (predicate != null)
                {
                    data = await DefaultRepository<TDbContext, TModel, TView>.Instance.GetSingleModelAsync(predicate);
                    _memoryCache.Set(cacheKey, data);
                }
                else
                {
                    data = new RepositoryResponse<TView>()
                    {
                        IsSucceed = true,
                        Data = DefaultRepository<TDbContext, TModel, TView>.Instance.ParseView(model)
                    };

                }
                if (!MixConstants.cachedKeys.Contains(cacheKey))
                {
                    MixConstants.cachedKeys.Add(cacheKey);
                }
                AlertAsync("Add Cache", 200, cacheKey);
            }
            data.LastUpdateConfiguration = MixService.GetConfig<DateTime?>("LastUpdateConfiguration");
            return data;
        }

        protected async Task<RepositoryResponse<TModel>> DeleteAsync<TView>(Expression<Func<TModel, bool>> predicate, bool isDeleteRelated = false)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            var data = await DefaultRepository<TDbContext, TModel, TView>.Instance.GetSingleModelAsync(predicate);
            if (data.IsSucceed)
            {
                RemoveCache();
                return await data.Data.RemoveModelAsync(isDeleteRelated).ConfigureAwait(false);
            }
            return new RepositoryResponse<TModel>() { IsSucceed = false };
        }

        protected async Task<RepositoryResponse<List<TModel>>> DeleteListAsync<TView>(bool isRemoveRelatedModel, Expression<Func<TModel, bool>> predicate, bool isDeleteRelated = false)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            var data = await DefaultRepository<TDbContext, TModel, TView>.Instance.RemoveListModelAsync(isRemoveRelatedModel, predicate);
            if (data.IsSucceed)
            {
                RemoveCache();

            }
            return data;
        }


        protected async Task<RepositoryResponse<FileViewModel>> ExportListAsync(Expression<Func<TModel, bool>> predicate, MixStructureType type)
        {
            var getData = await DefaultModelRepository<TDbContext, TModel>.Instance.GetModelListByAsync(predicate);
            if (getData.IsSucceed)
            {
                string exportPath = $"Exports/Structures/{typeof(TModel).Name}/{_lang}";
                string filename = $"{type.ToString()}_{DateTime.UtcNow.ToString("ddMMyyyy")}";
                var objContent = new JObject(
                    new JProperty("type", type.ToString()),
                    new JProperty("data", JArray.FromObject(getData.Data))
                    );
                var file = new FileViewModel()
                {
                    Filename = filename,
                    Extension = ".json",
                    FileFolder = exportPath,
                    Content = objContent.ToString()
                };
                // Copy current templates file
                FileRepository.Instance.SaveWebFile(file);
                return new RepositoryResponse<FileViewModel>()
                {
                    IsSucceed = true,
                    Data = file,
                };
            }
            return new RepositoryResponse<FileViewModel>();
        }
        protected async Task<RepositoryResponse<PaginationModel<TView>>> GetListAsync<TView>(string key, RequestPaging request, Expression<Func<TModel, bool>> predicate = null, TModel model = null)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            var getData = new RepositoryResponse<Lib.ViewModels.MixPages.ReadMvcViewModel>();
            var cacheKey = $"{typeof(TModel).Name}_list_{_lang}_{key}_{request.Status}_{request.Keyword}_{request.OrderBy}_{request.Direction}_{request.PageSize}_{request.PageIndex}_{request.Query}";
            var data = _memoryCache.Get<RepositoryResponse<PaginationModel<TView>>>(cacheKey);
            if (data == null)
            {
                if (predicate != null)
                {
                    data = await DefaultRepository<TDbContext, TModel, TView>.Instance.GetModelListByAsync(predicate, request.OrderBy, request.Direction, request.PageSize, request.PageIndex).ConfigureAwait(false);
                    _memoryCache.Set(cacheKey, data);
                }
                else
                {
                    data = await DefaultRepository<TDbContext, TModel, TView>.Instance.GetModelListAsync(request.OrderBy, request.Direction, request.PageSize, request.PageIndex).ConfigureAwait(false);
                    _memoryCache.Set(cacheKey, data);

                }
                if (!MixConstants.cachedKeys.Contains(cacheKey))
                {
                    MixConstants.cachedKeys.Add(cacheKey);
                }
                AlertAsync("Add Cache", 200, cacheKey);
            }
            data.LastUpdateConfiguration = MixService.GetConfig<DateTime?>("LastUpdateConfiguration");
            //AlertAsync("Get List Page", 200, $"Get {request.Key} list page");
            return data;
        }

        protected async Task<RepositoryResponse<TView>> SaveAsync<TView>(TView vm, bool isSaveSubModel)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            if (vm != null)
            {
                var result = await vm.SaveModelAsync(isSaveSubModel).ConfigureAwait(false);
                RemoveCache();
                return result;
            }
            return new RepositoryResponse<TView>();
        }

        protected async Task<RepositoryResponse<List<TView>>> SaveListAsync<TView>(List<TView> lstVm, bool isSaveSubModel)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            var result = new RepositoryResponse<List<TView>>() { IsSucceed = true };
            if (lstVm != null)
            {
                foreach (var vm in lstVm)
                {
                    var tmp = await vm.SaveModelAsync(isSaveSubModel).ConfigureAwait(false);
                    result.IsSucceed = result.IsSucceed && tmp.IsSucceed;
                    if (!tmp.IsSucceed)
                    {
                        result.Exception = tmp.Exception;
                        result.Errors.AddRange(tmp.Errors);
                    }
                }
                RemoveCache();
                return result;
            }
            return result;
        }
        protected RepositoryResponse<List<TView>> SaveList<TView>(List<TView> lstVm, bool isSaveSubModel)
            where TView : ViewModelBase<TDbContext, TModel, TView>
        {
            var result = new RepositoryResponse<List<TView>>() { IsSucceed = true };
            if (lstVm != null)
            {
                foreach (var vm in lstVm)
                {
                    var tmp = vm.SaveModel(isSaveSubModel);
                    result.IsSucceed = result.IsSucceed && tmp.IsSucceed;
                    if (!tmp.IsSucceed)
                    {
                        result.Exception = tmp.Exception;
                        result.Errors.AddRange(tmp.Errors);
                    }
                }
                RemoveCache();
                return result;
            }
            return result;
        }

        public JObject SaveEncrypt([FromBody] RequestEncrypted request)
        {
            //var key = Convert.FromBase64String(request.Key); //Encoding.UTF8.GetBytes(request.Key);
            //var iv = Convert.FromBase64String(request.IV); //Encoding.UTF8.GetBytes(request.IV);
            string encrypted = string.Empty;
            string decrypt = string.Empty;
            if (!string.IsNullOrEmpty(request.PlainText))
            {
                encrypted = MixService.EncryptStringToBytes_Aes(new JObject()).ToString();
            }
            if (!string.IsNullOrEmpty(request.Encrypted))
            {
                //decrypt = MixService.DecryptStringFromBytes_Aes(request.Encrypted, request.Key, request.IV);
            }
            JObject data = new JObject(
                new JProperty("key", request.Key),
                new JProperty("encrypted", encrypted),
                new JProperty("plainText", decrypt));

            return data;
        }

        protected void RemoveCache()
        {
            foreach (var item in MixConstants.cachedKeys)
            {
                _memoryCache.Remove(item);
            }
            MixConstants.cachedKeys = new List<string>();
            AlertAsync("Empty Cache", 200);
        }
        protected void AlertAsync(string action, int status, string message = null)
        {
            var logMsg = new JObject()
                {
                    new JProperty("created_at", DateTime.UtcNow),
                    new JProperty("ip_address", Request.HttpContext.Connection.RemoteIpAddress.ToString()),
                    new JProperty("user", User.Identity?.Name?? User.Claims.SingleOrDefault(c=>c.Type == "Username")?.Value),
                    new JProperty("request_url", Request.Path.Value),
                    new JProperty("action", action),
                    new JProperty("status", status),
                    new JProperty("message", message)
                };
            _hubContext.Clients.All.SendAsync("ReceiveMessage", logMsg);
        }

        protected void ParseRequestPagingDate(RequestPaging request)
        {
            request.FromDate = request.FromDate.HasValue ? new DateTime(request.FromDate.Value.Year, request.FromDate.Value.Month, request.FromDate.Value.Day).ToUniversalTime()
                : default(DateTime?);
            request.ToDate = request.ToDate.HasValue ? new DateTime(request.ToDate.Value.Year, request.ToDate.Value.Month, request.ToDate.Value.Day).ToUniversalTime().AddDays(1)
                : default(DateTime?);
        }
        protected QueryString ParseQuery(RequestPaging request)
        {
            return new QueryString(request.Query);
        }
        /// <summary>
        /// Gets the language.
        /// </summary>
        protected void GetLanguage()
        {
            _lang = RouteData?.Values["culture"] != null ? RouteData.Values["culture"].ToString() : MixService.GetConfig<string>("Language");
            ViewBag.culture = _lang;
            _domain = string.Format("{0}://{1}", Request.Scheme, Request.Host);
        }
    }
}
