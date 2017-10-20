namespace Sitecore.Support.ContentSearch.Client.Pipelines.Search
{
    using Sitecore.ContentSearch;
    using Sitecore.ContentSearch.Abstractions;
    using Sitecore.ContentSearch.Client.Pipelines.Search;
    using Sitecore.ContentSearch.SearchTypes;
    using Sitecore.ContentSearch.Security;
    using Sitecore.ContentSearch.Utilities;
    using Sitecore.Data.Items;
    using Sitecore.Diagnostics;
    using Sitecore.Pipelines.Search;
    using Sitecore.Search;
    using Sitecore.Shell;
    using Sitecore.StringExtensions;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    internal class SearchContentSearchIndex
    {
        private ISettings settings;

        public SearchContentSearchIndex()
        {
        }

        internal SearchContentSearchIndex(ISettings settings)
        {
            this.settings = settings;
        }

        private bool IsHidden(Item item)
        {
            Assert.ArgumentNotNull(item, "item");
            return (item.Appearance.Hidden || ((item.Parent != null) && this.IsHidden(item.Parent)));
        }

        protected virtual void FillSearchResult(IList<SitecoreUISearchResultItem> searchResult, SearchArgs args)
        {
            foreach (var result in searchResult)
            {
                var sitecoreItem = this.GetSitecoreItem(result);
                if (sitecoreItem == null)
                {
                    // item either does not exist or security protected.
                    // According to the requirements from management, defined in the TFS:96901
                    // processor should not return non-Sitecore items.
                    continue;
                }

                var title = result.DisplayName ?? result.Name;
                if (title == null)
                {
                    continue;
                }

                object icon = result.Fields.Find(pair => pair.Key == Sitecore.ContentSearch.BuiltinFields.Icon).Value
                            ?? sitecoreItem.Appearance.Icon ?? this.settings?.DefaultIcon();

                if (icon == null)
                {
                    continue;
                }

                string url = string.Empty;
                if (result.Uri != null)
                {
                    url = result.Uri.ToString();
                }

                args.Result.AddResult(new SearchResult(title, icon.ToString(), url));
            }
        }

        protected virtual Item GetSitecoreItem(SitecoreUISearchResultItem searchItem)
        {
            if (searchItem == null)
            {
                return null;
            }

            try
            {
                return searchItem.GetItem();
            }
            catch (NullReferenceException)
            {
            }

            return null;
        }

        public virtual void Process(SearchArgs args)
        {
            Assert.ArgumentNotNull(args, "args");
            if (!args.UseLegacySearchEngine)
            {
                if (!ContentSearchManager.Locator.GetInstance<IContentSearchConfigurationSettings>().ItemBucketsEnabled())
                {
                    args.UseLegacySearchEngine = true;
                }
                else
                {                    
                    Item item = args.Database.GetRootItem();
                    Assert.IsNotNull(item, "rootItem");
                    if (!args.TextQuery.IsNullOrEmpty())
                    {
                        ISearchIndex index = ContentSearchManager.GetIndex(new SitecoreIndexableItem(item));
                        if (this.settings == null)
                        {
                            this.settings = index.Locator.GetInstance<ISettings>();
                        }
                        using (IProviderSearchContext context = index.CreateSearchContext(SearchSecurityOptions.Default))
                        {
                            Func<SitecoreUISearchResultItem, bool> predicate = null;
                            List<SitecoreUISearchResultItem> results = new List<SitecoreUISearchResultItem>();
                            try
                            {
                                IQueryable<SitecoreUISearchResultItem> source = null;
                                if (args.Type != SearchType.ContentEditor)
                                {
                                    source = new GenericSearchIndex().Search(args, context);
                                }
                                if ((source == null) || (source.Count<SitecoreUISearchResultItem>() == 0))
                                {
                                    if ((args.ContentLanguage != null) && !args.ContentLanguage.Name.IsNullOrEmpty())
                                    {
                                        source = from i in context.GetQueryable<SitecoreUISearchResultItem>()
                                                 where i.Name.StartsWith(args.TextQuery) || (i.Content.Contains(args.TextQuery) && i.Language.Equals(args.ContentLanguage.Name))
                                                 select i;
                                    }
                                    else
                                    {
                                        source = from i in context.GetQueryable<SitecoreUISearchResultItem>()
                                                 where i.Name.StartsWith(args.TextQuery) || i.Content.Contains(args.TextQuery)
                                                 select i;
                                    }
                                }
                                if ((args.Root != null) && (args.Type != SearchType.ContentEditor))
                                {
                                    source = from i in source
                                             where i.Paths.Contains(args.Root.ID)
                                             select i;
                                }
                                if (predicate == null)
                                {
                                    predicate = result => results.Count < args.Limit;
                                }
                                using (IEnumerator<SitecoreUISearchResultItem> enumerator = source.TakeWhile<SitecoreUISearchResultItem>(predicate).GetEnumerator())
                                {
                                    while (enumerator.MoveNext())
                                    {
                                        Func<SitecoreUISearchResultItem, bool> func = null;
                                        SitecoreUISearchResultItem result = enumerator.Current;
                                        if (!UserOptions.View.ShowHiddenItems)
                                        {
                                            var item2 = this.GetSitecoreItem(result);
                                            if ((item2 != null) && this.IsHidden(item2))
                                            {
                                                continue;
                                            }
                                        }
                                        if (func == null)
                                        {
                                            func = r => r.ItemId == result.ItemId;
                                        }
                                        SitecoreUISearchResultItem item3 = results.FirstOrDefault<SitecoreUISearchResultItem>(func);
                                        if (item3 == null)
                                        {
                                            results.Add(result);
                                        }
                                        else
                                        {
                                            if ((args.ContentLanguage != null) && !args.ContentLanguage.Name.IsNullOrEmpty())
                                            {
                                                if (((item3.Language != args.ContentLanguage.Name) && (result.Language == args.ContentLanguage.Name)) || ((item3.Language == result.Language) && (item3.Uri.Version.Number < result.Uri.Version.Number)))
                                                {
                                                    results.Remove(item3);
                                                    results.Add(result);
                                                }
                                                continue;
                                            }
                                            if (args.Type != SearchType.Classic)
                                            {
                                                if ((item3.Language == result.Language) && (item3.Uri.Version.Number < result.Uri.Version.Number))
                                                {
                                                    results.Remove(item3);
                                                    results.Add(result);
                                                }
                                            }
                                            else
                                            {
                                                results.Add(result);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception exception)
                            {
                                Log.Error("Invalid lucene search query: " + args.TextQuery, exception, this);
                                return;
                            }
                            this.FillSearchResult(results, args);
                        }
                    }
                }
            }
        }
    }
}