﻿using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Mix.Domain.Core.ViewModels;
using Mix.Cms.Lib.Models.Cms;
using Mix.Cms.Lib.ViewModels;
using Mix.Cms.Lib.Models.Account;
using System;
using System.Linq;
using System.Threading.Tasks;
using static Mix.Cms.Lib.MixEnums;
using Mix.Cms.Lib.Repositories;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using Mix.Cms.Messenger.Models.Data;
using Mix.Common.Helper;

namespace Mix.Cms.Lib.Services
{
    public class InitCmsService
    {
        public InitCmsService()
        {
        }

        public async Task<RepositoryResponse<bool>> InitCms(string siteName, InitCulture culture)
        {
            RepositoryResponse<bool> result = new RepositoryResponse<bool>();
            MixCmsContext context = null;
            MixCmsAccountContext accountContext = null;
            MixChatServiceContext messengerContext;
            IDbContextTransaction transaction = null;
            IDbContextTransaction accTransaction = null;
            bool isSucceed = true;
            try
            {
                if (!string.IsNullOrEmpty(MixService.GetConnectionString(MixConstants.CONST_CMS_CONNECTION)))
                {
                    context = new MixCmsContext();
                    accountContext = new MixCmsAccountContext();
                    messengerContext = new MixChatServiceContext();
                    //MixChatServiceContext._cnn = MixService.GetConnectionString(MixConstants.CONST_CMS_CONNECTION);
                    await context.Database.MigrateAsync();
                    await accountContext.Database.MigrateAsync();
                    await messengerContext.Database.MigrateAsync();
                    transaction = context.Database.BeginTransaction();

                    var countCulture = context.MixCulture.Count();

                    var isInit = countCulture > 0;

                    if (!isInit)
                    {
                        MixService.SetConfig<string>("SiteName", siteName);
                        isSucceed = InitCultures(culture, context, transaction);
                        if (isSucceed)
                        {
                            isSucceed = isSucceed && InitPositions(context, transaction);
                        }
                        else
                        {
                            result.Errors.Add("Cannot init Cultures");
                        }
                        if (isSucceed)
                        {
                            isSucceed = isSucceed && await InitConfigurationsAsync(siteName, culture, context, transaction);
                        }
                        else
                        {
                            result.Errors.Add("Cannot init Positions");
                        }
                        if (isSucceed)
                        {
                            isSucceed = isSucceed && await InitLanguagesAsync(culture, context, transaction);
                        }
                        else
                        {
                            result.Errors.Add("Cannot init Configurations");
                        }
                        if (isSucceed)
                        {
                            var initTheme = await InitThemesAsync(siteName, context, transaction);
                            isSucceed = isSucceed && initTheme.IsSucceed;
                            result.Errors.AddRange(initTheme.Errors);
                            result.Exception = initTheme.Exception;
                        }
                        else
                        {                            
                            result.Errors.Add("Cannot init Languages");
                        }
                    }
                    else
                    {
                        isSucceed = true;
                    }

                    if (isSucceed && context.MixPage.Count() == 0)
                    {
                        int id = 0;
                        InitPage(ref id, "Home", "Pages/_Home.cshtml", "/assets/img/bgs/home.jpeg"
                            , MixPageType.Home, culture.Specificulture, context, transaction);
                        InitPage(ref id, "Tag", "Pages/_Tag.cshtml", "/assets/img/bgs/tag.jpg"
                            , MixPageType.Article, culture.Specificulture, context, transaction);
                        InitPage(ref id, "Search", "Pages/_Search.cshtml", "/assets/img/bgs/search.jpg"
                            , MixPageType.Article, culture.Specificulture, context, transaction);
                        InitPage(ref id, "Maintenance", "Pages/_Maintenance.cshtml", "/assets/img/bgs/maintenance.jpg"
                            , MixPageType.Article, culture.Specificulture, context, transaction);
                        InitPage(ref id, "404", "Pages/_404.cshtml", "/assets/img/bgs/404.jpg"
                            , MixPageType.Article, culture.Specificulture, context, transaction);
                        InitPage(ref id, "403", "Pages/_403.cshtml", "/assets/img/bgs/403.jpg"
                            , MixPageType.Article, culture.Specificulture, context, transaction);
                        isSucceed = (await context.SaveChangesAsync().ConfigureAwait(false)) > 0;
                    }
                    else
                    {
                        result.Errors.Add("Cannot init Themes");
                    }
                    if (isSucceed)
                    {
                        transaction.Commit();
                    }
                    else
                    {
                        transaction.Rollback();
                    }
                }
                result.IsSucceed = isSucceed;
                return result;
            }
            catch (Exception ex) // TODO: Add more specific exeption types instead of Exception only
            {
                transaction?.Rollback();
                accTransaction?.Rollback();
                result.IsSucceed = false;
                result.Exception = ex;
                return result;
            }
            finally
            {
                context?.Dispose();
                accountContext?.Dispose();
            }
        }


        private async Task<bool> InitConfigurationsAsync(string siteName, InitCulture culture, MixCmsContext context, IDbContextTransaction transaction)
        {
            /* Init Configs */
            var configurations = FileRepository.Instance.GetFile(MixConstants.CONST_FILE_CONFIGURATIONS, "data", true, "{}");
            var obj = JObject.Parse(configurations.Content);
            var arrConfiguration = obj["data"].ToObject<List<MixConfiguration>>();
            if (!string.IsNullOrEmpty(siteName))
            {
                arrConfiguration.Find(c => c.Keyword == "SiteName").Value = siteName;
                arrConfiguration.Find(c => c.Keyword == "ThemeName").Value = Common.Helper.SeoHelper.GetSEOString(siteName);
                arrConfiguration.Find(c => c.Keyword == "ThemeFolder").Value = Common.Helper.SeoHelper.GetSEOString(siteName);
            }
            var result = await ViewModels.MixConfigurations.ReadMvcViewModel.ImportConfigurations(arrConfiguration, culture.Specificulture, context, transaction);
            return result.IsSucceed;

        }

        private async Task<bool> InitLanguagesAsync(InitCulture culture, MixCmsContext context, IDbContextTransaction transaction)
        {
            /* Init Languages */
            var configurations = FileRepository.Instance.GetFile(MixConstants.CONST_FILE_LANGUAGES, "data", true, "{}");
            var obj = JObject.Parse(configurations.Content);
            var arrLanguage = obj["data"].ToObject<List<MixLanguage>>();
            var result = await ViewModels.MixLanguages.ReadMvcViewModel.ImportLanguages(arrLanguage, culture.Specificulture, context, transaction);
            return result.IsSucceed;

        }

        private async Task<RepositoryResponse<ViewModels.MixThemes.InitViewModel>> InitThemesAsync(string siteName, MixCmsContext context, IDbContextTransaction transaction)
        {
            var getThemes = ViewModels.MixThemes.InitViewModel.Repository.GetModelList(_context: context, _transaction: transaction);
            if (!context.MixTheme.Any())
            {
                ViewModels.MixThemes.InitViewModel theme = new ViewModels.MixThemes.InitViewModel(new MixTheme()
                {
                    Id = 1,
                    Title = siteName,
                    Name = SeoHelper.GetSEOString(siteName),
                    CreatedDateTime = DateTime.UtcNow,
                    CreatedBy = "Admin",
                    Status = (int)MixContentStatus.Published,
                }, context, transaction);

                return await theme.SaveModelAsync(true, context, transaction);
            }

            return new RepositoryResponse<ViewModels.MixThemes.InitViewModel>() { IsSucceed = true };
        }

        protected bool InitCultures(InitCulture culture, MixCmsContext context, IDbContextTransaction transaction)
        {
            bool isSucceed = true;
            try
            {
                if (context.MixCulture.Count() == 0)
                {

                    // EN-US

                    var enCulture = new MixCulture()
                    {
                        Id = 1,
                        Specificulture = culture.Specificulture,
                        FullName = culture.FullName,
                        Description = culture.Description,
                        Icon = culture.Icon,
                        Alias = culture.Alias,
                        Status = (int)MixEnums.MixContentStatus.Published,
                        CreatedDateTime = DateTime.UtcNow
                    };
                    context.Entry(enCulture).State = EntityState.Added;

                    context.SaveChanges();
                }
            }
            catch
            {
                isSucceed = false;
            }
            return isSucceed;
        }

        protected bool InitPositions(MixCmsContext context, IDbContextTransaction transaction)
        {
            bool isSucceed = true;
            var count = context.MixPortalPage.Count();
            if (count == 0)
            {
                var p = new MixPosition()
                {
                    Id = 1,
                    Description = nameof(MixEnums.CatePosition.Nav)
                };
                context.Entry(p).State = EntityState.Added;
                p = new MixPosition()
                {
                    Id = 2,
                    Description = nameof(MixEnums.CatePosition.Top)
                };
                context.Entry(p).State = EntityState.Added;
                p = new MixPosition()
                {
                    Id = 3,
                    Description = nameof(MixEnums.CatePosition.Left)
                };
                context.Entry(p).State = EntityState.Added;
                p = new MixPosition()
                {
                    Id = 4,
                    Description = nameof(MixEnums.CatePosition.Footer)
                };
                context.Entry(p).State = EntityState.Added;

                context.SaveChanges();
            }
            return isSucceed;
        }

        protected void InitPage(ref int id, string title, string template, string image, MixPageType type, string culture
            , MixCmsContext context, IDbContextTransaction transaction)
        {
            id += 1;
            var cate = new MixPage()
            {
                Id = id,
                Level = 0,
                Title = title,
                SeoName = title.ToLower(),
                SeoDescription = title.ToLower(),
                SeoKeywords = title.ToLower(),
                SeoTitle = title.ToLower(),
                Specificulture = culture,
                Template = template,
                Image = image,
                Type = (int)type,
                CreatedBy = "Admin",
                CreatedDateTime = DateTime.UtcNow,
                Status = (int)PageStatus.Published
            };
            context.Entry(cate).State = EntityState.Added;
            var alias = new MixUrlAlias()
            {
                Id = id,
                SourceId = id.ToString(),
                Type = (int)UrlAliasType.Page,
                Specificulture = culture,
                CreatedDateTime = DateTime.UtcNow,
                Alias = cate.Title.ToLower(),
                Status = (int)MixContentStatus.Published
            };
            context.Entry(alias).State = EntityState.Added;
        }
    }
}
