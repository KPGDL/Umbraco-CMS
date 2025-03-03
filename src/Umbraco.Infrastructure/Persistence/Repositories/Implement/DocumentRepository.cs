using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence.Dtos;
using Umbraco.Cms.Infrastructure.Persistence.Factories;
using Umbraco.Cms.Infrastructure.Persistence.Querying;
using Umbraco.Cms.Infrastructure.Persistence.SqlSyntax;
using Umbraco.Extensions;

namespace Umbraco.Cms.Infrastructure.Persistence.Repositories.Implement
{
    /// <summary>
    /// Represents a repository for doing CRUD operations for <see cref="IContent"/>.
    /// </summary>
    public class DocumentRepository : ContentRepositoryBase<int, IContent, DocumentRepository>, IDocumentRepository
    {
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly ITemplateRepository _templateRepository;
        private readonly ITagRepository _tagRepository;
        private readonly IJsonSerializer _serializer;
        private readonly AppCaches _appCaches;
        private readonly ILoggerFactory _loggerFactory;
        private PermissionRepository<IContent> _permissionRepository;
        private readonly ContentByGuidReadRepository _contentByGuidReadRepository;
        private readonly IScopeAccessor _scopeAccessor;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="scopeAccessor"></param>
        /// <param name="appCaches"></param>
        /// <param name="logger"></param>
        /// <param name="loggerFactory"></param>
        /// <param name="contentTypeRepository"></param>
        /// <param name="templateRepository"></param>
        /// <param name="tagRepository"></param>
        /// <param name="languageRepository"></param>
        /// <param name="propertyEditors">
        ///     Lazy property value collection - must be lazy because we have a circular dependency since some property editors require services, yet these services require property editors
        /// </param>
        public DocumentRepository(
            IScopeAccessor scopeAccessor,
            AppCaches appCaches,
            ILogger<DocumentRepository> logger,
            ILoggerFactory loggerFactory,
            IContentTypeRepository contentTypeRepository,
            ITemplateRepository templateRepository,
            ITagRepository tagRepository,
            ILanguageRepository languageRepository,
            IRelationRepository relationRepository,
            IRelationTypeRepository relationTypeRepository,
            PropertyEditorCollection propertyEditors,
            DataValueReferenceFactoryCollection dataValueReferenceFactories,
            IDataTypeService dataTypeService,
            IJsonSerializer serializer,
            IEventAggregator eventAggregator)
            : base(scopeAccessor, appCaches, logger, languageRepository, relationRepository, relationTypeRepository, propertyEditors, dataValueReferenceFactories, dataTypeService, eventAggregator)
        {
            _contentTypeRepository = contentTypeRepository ?? throw new ArgumentNullException(nameof(contentTypeRepository));
            _templateRepository = templateRepository ?? throw new ArgumentNullException(nameof(templateRepository));
            _tagRepository = tagRepository ?? throw new ArgumentNullException(nameof(tagRepository));
            _serializer = serializer;
            _appCaches = appCaches;
            _loggerFactory = loggerFactory;
            _scopeAccessor = scopeAccessor;
            _contentByGuidReadRepository = new ContentByGuidReadRepository(this, scopeAccessor, appCaches, loggerFactory.CreateLogger<ContentByGuidReadRepository>());
        }

        protected override DocumentRepository This => this;

        /// <summary>
        /// Default is to always ensure all documents have unique names
        /// </summary>
        protected virtual bool EnsureUniqueNaming { get; } = true;

        // note: is ok to 'new' the repo here as it's a sub-repo really
        private PermissionRepository<IContent> PermissionRepository => _permissionRepository
            ?? (_permissionRepository = new PermissionRepository<IContent>(_scopeAccessor, _appCaches, _loggerFactory.CreateLogger<PermissionRepository<IContent>>()));

        #region Repository Base

        protected override Guid NodeObjectTypeId => Cms.Core.Constants.ObjectTypes.Document;

        protected override IContent PerformGet(int id)
        {
            var sql = GetBaseQuery(QueryType.Single)
                .Where<NodeDto>(x => x.NodeId == id)
                .SelectTop(1);

            var dto = Database.Fetch<DocumentDto>(sql).FirstOrDefault();
            return dto == null
                ? null
                : MapDtoToContent(dto);
        }

        protected override IEnumerable<IContent> PerformGetAll(params int[] ids)
        {
            var sql = GetBaseQuery(QueryType.Many);

            if (ids.Any())
                sql.WhereIn<NodeDto>(x => x.NodeId, ids);

            return MapDtosToContent(Database.Fetch<DocumentDto>(sql));
        }

        protected override IEnumerable<IContent> PerformGetByQuery(IQuery<IContent> query)
        {
            var sqlClause = GetBaseQuery(QueryType.Many);

            var translator = new SqlTranslator<IContent>(sqlClause, query);
            var sql = translator.Translate();

            AddGetByQueryOrderBy(sql);

            return MapDtosToContent(Database.Fetch<DocumentDto>(sql));
        }

        private void AddGetByQueryOrderBy(Sql<ISqlContext> sql)
        {
            sql
                .OrderBy<NodeDto>(x => x.Level)
                .OrderBy<NodeDto>(x => x.SortOrder);
        }

        protected override Sql<ISqlContext> GetBaseQuery(QueryType queryType)
        {
            return GetBaseQuery(queryType, true);
        }

        // gets the COALESCE expression for variant/invariant name
        private string VariantNameSqlExpression
            => SqlContext.VisitDto<ContentVersionCultureVariationDto, NodeDto>((ccv, node) => ccv.Name ?? node.Text, "ccv").Sql;

        protected Sql<ISqlContext> GetBaseQuery(QueryType queryType, bool current)
        {
            var sql = SqlContext.Sql();

            switch (queryType)
            {
                case QueryType.Count:
                    sql = sql.SelectCount();
                    break;
                case QueryType.Ids:
                    sql = sql.Select<DocumentDto>(x => x.NodeId);
                    break;
                case QueryType.Single:
                case QueryType.Many:
                    // R# may flag this ambiguous and red-squiggle it, but it is not
                    sql = sql.Select<DocumentDto>(r =>
                       r.Select(documentDto => documentDto.ContentDto, r1 =>
                           r1.Select(contentDto => contentDto.NodeDto))
                        .Select(documentDto => documentDto.DocumentVersionDto, r1 =>
                           r1.Select(documentVersionDto => documentVersionDto.ContentVersionDto))
                        .Select(documentDto => documentDto.PublishedVersionDto, "pdv", r1 =>
                           r1.Select(documentVersionDto => documentVersionDto.ContentVersionDto, "pcv")))

                       // select the variant name, coalesce to the invariant name, as "variantName"
                       .AndSelect(VariantNameSqlExpression + " AS variantName");
                    break;
            }

            sql
                .From<DocumentDto>()
                .InnerJoin<ContentDto>().On<DocumentDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                .InnerJoin<NodeDto>().On<ContentDto, NodeDto>(left => left.NodeId, right => right.NodeId)

                // inner join on mandatory edited version
                .InnerJoin<ContentVersionDto>()
                    .On<DocumentDto, ContentVersionDto>((left, right) => left.NodeId == right.NodeId)
                .InnerJoin<DocumentVersionDto>()
                    .On<ContentVersionDto, DocumentVersionDto>((left, right) => left.Id == right.Id)

                // left join on optional published version
                .LeftJoin<ContentVersionDto>(nested =>
                    nested.InnerJoin<DocumentVersionDto>("pdv")
                            .On<ContentVersionDto, DocumentVersionDto>((left, right) => left.Id == right.Id && right.Published, "pcv", "pdv"), "pcv")
                    .On<DocumentDto, ContentVersionDto>((left, right) => left.NodeId == right.NodeId, aliasRight: "pcv")

                // TODO: should we be joining this when the query type is not single/many?
                // left join on optional culture variation
                //the magic "[[[ISOCODE]]]" parameter value will be replaced in ContentRepositoryBase.GetPage() by the actual ISO code
                .LeftJoin<ContentVersionCultureVariationDto>(nested =>
                    nested.InnerJoin<LanguageDto>("lang").On<ContentVersionCultureVariationDto, LanguageDto>((ccv, lang) => ccv.LanguageId == lang.Id && lang.IsoCode == "[[[ISOCODE]]]", "ccv", "lang"), "ccv")
                    .On<ContentVersionDto, ContentVersionCultureVariationDto>((version, ccv) => version.Id == ccv.VersionId, aliasRight: "ccv");

            sql
                .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId);

            // this would ensure we don't get the published version - keep for reference
            //sql
            //    .WhereAny(
            //        x => x.Where<ContentVersionDto, ContentVersionDto>((x1, x2) => x1.Id != x2.Id, alias2: "pcv"),
            //        x => x.WhereNull<ContentVersionDto>(x1 => x1.Id, "pcv")
            //    );

            if (current)
                sql.Where<ContentVersionDto>(x => x.Current); // always get the current version

            return sql;
        }

        protected override Sql<ISqlContext> GetBaseQuery(bool isCount)
        {
            return GetBaseQuery(isCount ? QueryType.Count : QueryType.Single);
        }

        // ah maybe not, that what's used for eg Exists in base repo
        protected override string GetBaseWhereClause()
        {
            return $"{Cms.Core.Constants.DatabaseSchema.Tables.Node}.id = @id";
        }

        protected override IEnumerable<string> GetDeleteClauses()
        {
            var list = new List<string>
            {
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.ContentSchedule + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.RedirectUrl + " WHERE contentKey IN (SELECT uniqueId FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Node + " WHERE id = @id)",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.User2NodeNotify + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.UserGroup2NodePermission + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.UserStartNode + " WHERE startNode = @id",
                "UPDATE " + Cms.Core.Constants.DatabaseSchema.Tables.UserGroup + " SET startContentId = NULL WHERE startContentId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Relation + " WHERE parentId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Relation + " WHERE childId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.TagRelationship + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Domain + " WHERE domainRootStructureID = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Document + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.DocumentCultureVariation + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.DocumentVersion + " WHERE id IN (SELECT id FROM " + Cms.Core.Constants.DatabaseSchema.Tables.ContentVersion + " WHERE nodeId = @id)",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.PropertyData + " WHERE versionId IN (SELECT id FROM " + Cms.Core.Constants.DatabaseSchema.Tables.ContentVersion + " WHERE nodeId = @id)",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.ContentVersionCultureVariation + " WHERE versionId IN (SELECT id FROM " + Cms.Core.Constants.DatabaseSchema.Tables.ContentVersion + " WHERE nodeId = @id)",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.ContentVersion + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Content + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.AccessRule + " WHERE accessId IN (SELECT id FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Access + " WHERE nodeId = @id OR loginNodeId = @id OR noAccessNodeId = @id)",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Access + " WHERE nodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Access + " WHERE loginNodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Access + " WHERE noAccessNodeId = @id",
                "DELETE FROM " + Cms.Core.Constants.DatabaseSchema.Tables.Node + " WHERE id = @id"
            };
            return list;
        }

        #endregion

        #region Versions

        public override IEnumerable<IContent> GetAllVersions(int nodeId)
        {
            var sql = GetBaseQuery(QueryType.Many, false)
                .Where<NodeDto>(x => x.NodeId == nodeId)
                .OrderByDescending<ContentVersionDto>(x => x.Current)
                .AndByDescending<ContentVersionDto>(x => x.VersionDate);

            return MapDtosToContent(Database.Fetch<DocumentDto>(sql), true);
        }

        // TODO: This method needs to return a readonly version of IContent! The content returned
        // from this method does not contain all of the data required to re-persist it and if that
        // is attempted some odd things will occur.
        // Either we create an IContentReadOnly (which ultimately we should for vNext so we can
        // differentiate between methods that return entities that can be re-persisted or not), or
        // in the meantime to not break API compatibility, we can add a property to IContentBase
        // (or go further and have it on IUmbracoEntity): "IsReadOnly" and if that is true we throw
        // an exception if that entity is passed to a Save method.
        // Ideally we return "Slim" versions of content for all sorts of methods here and in ContentService.
        // Perhaps another non-breaking alternative is to have new services like IContentServiceReadOnly
        // which can return IContentReadOnly.
        // We have the ability with `MapDtosToContent` to reduce the amount of data looked up for a
        // content item. Ideally for paged data that populates list views, these would be ultra slim
        // content items, there's no reason to populate those with really anything apart from property data,
        // but until we do something like the above, we can't do that since it would be breaking and unclear.
        public override IEnumerable<IContent> GetAllVersionsSlim(int nodeId, int skip, int take)
        {
            var sql = GetBaseQuery(QueryType.Many, false)
                .Where<NodeDto>(x => x.NodeId == nodeId)
                .OrderByDescending<ContentVersionDto>(x => x.Current)
                .AndByDescending<ContentVersionDto>(x => x.VersionDate);

            return MapDtosToContent(Database.Fetch<DocumentDto>(sql), true,
                // load bare minimum, need variants though since this is used to rollback with variants
                false, false, false, true).Skip(skip).Take(take);
        }

        public override IContent GetVersion(int versionId)
        {
            var sql = GetBaseQuery(QueryType.Single, false)
                .Where<ContentVersionDto>(x => x.Id == versionId);

            var dto = Database.Fetch<DocumentDto>(sql).FirstOrDefault();
            return dto == null ? null : MapDtoToContent(dto);
        }

        // deletes a specific version
        public override void DeleteVersion(int versionId)
        {
            // TODO: test object node type?

            // get the version we want to delete
            var template = SqlContext.Templates.Get("Umbraco.Core.DocumentRepository.GetVersion", tsql =>
                tsql.Select<ContentVersionDto>()
                    .AndSelect<DocumentVersionDto>()
                    .From<ContentVersionDto>()
                    .InnerJoin<DocumentVersionDto>()
                    .On<ContentVersionDto, DocumentVersionDto>((c, d) => c.Id == d.Id)
                    .Where<ContentVersionDto>(x => x.Id == SqlTemplate.Arg<int>("versionId"))
            );
            var versionDto = Database.Fetch<DocumentVersionDto>(template.Sql(new { versionId })).FirstOrDefault();

            // nothing to delete
            if (versionDto == null)
                return;

            // don't delete the current or published version
            if (versionDto.ContentVersionDto.Current)
                throw new InvalidOperationException("Cannot delete the current version.");
            else if (versionDto.Published)
                throw new InvalidOperationException("Cannot delete the published version.");

            PerformDeleteVersion(versionDto.ContentVersionDto.NodeId, versionId);
        }

        //  deletes all versions of an entity, older than a date.
        public override void DeleteVersions(int nodeId, DateTime versionDate)
        {
            // TODO: test object node type?

            // get the versions we want to delete, excluding the current one
            var template = SqlContext.Templates.Get("Umbraco.Core.DocumentRepository.GetVersions", tsql =>
               tsql.Select<ContentVersionDto>()
                    .From<ContentVersionDto>()
                    .InnerJoin<DocumentVersionDto>()
                    .On<ContentVersionDto, DocumentVersionDto>((c, d) => c.Id == d.Id)
                    .Where<ContentVersionDto>(x => x.NodeId == SqlTemplate.Arg<int>("nodeId") && !x.Current && x.VersionDate < SqlTemplate.Arg<DateTime>("versionDate"))
                    .Where<DocumentVersionDto>(x => !x.Published)
            );
            var versionDtos = Database.Fetch<ContentVersionDto>(template.Sql(new { nodeId, versionDate }));
            foreach (var versionDto in versionDtos)
                PerformDeleteVersion(versionDto.NodeId, versionDto.Id);
        }

        protected override void PerformDeleteVersion(int id, int versionId)
        {
            Database.Delete<PropertyDataDto>("WHERE versionId = @versionId", new { versionId });
            Database.Delete<ContentVersionCultureVariationDto>("WHERE versionId = @versionId", new { versionId });
            Database.Delete<DocumentVersionDto>("WHERE id = @versionId", new { versionId });
            Database.Delete<ContentVersionDto>("WHERE id = @versionId", new { versionId });
        }

        #endregion

        #region Persist

        protected override void PersistNewItem(IContent entity)
        {
            entity.AddingEntity();

            var publishing = entity.PublishedState == PublishedState.Publishing;

            // ensure that the default template is assigned
            if (entity.TemplateId.HasValue == false)
                entity.TemplateId = entity.ContentType.DefaultTemplate?.Id;

            // sanitize names
            SanitizeNames(entity, publishing);

            // ensure that strings don't contain characters that are invalid in xml
            // TODO: do we really want to keep doing this here?
            entity.SanitizeEntityPropertiesForXmlStorage();

            // create the dto
            var dto = ContentBaseFactory.BuildDto(entity, NodeObjectTypeId);

            // derive path and level from parent
            var parent = GetParentNodeDto(entity.ParentId);
            var level = parent.Level + 1;

            // get sort order
            var sortOrder = GetNewChildSortOrder(entity.ParentId, 0);

            // persist the node dto
            var nodeDto = dto.ContentDto.NodeDto;
            nodeDto.Path = parent.Path;
            nodeDto.Level = Convert.ToInt16(level);
            nodeDto.SortOrder = sortOrder;

            // see if there's a reserved identifier for this unique id
            // and then either update or insert the node dto
            var id = GetReservedId(nodeDto.UniqueId);
            if (id > 0)
                nodeDto.NodeId = id;
            else
                Database.Insert(nodeDto);

            nodeDto.Path = string.Concat(parent.Path, ",", nodeDto.NodeId);
            nodeDto.ValidatePathWithException();
            Database.Update(nodeDto);

            // update entity
            entity.Id = nodeDto.NodeId;
            entity.Path = nodeDto.Path;
            entity.SortOrder = sortOrder;
            entity.Level = level;

            // persist the content dto
            var contentDto = dto.ContentDto;
            contentDto.NodeId = nodeDto.NodeId;
            Database.Insert(contentDto);

            // persist the content version dto
            var contentVersionDto = dto.DocumentVersionDto.ContentVersionDto;
            contentVersionDto.NodeId = nodeDto.NodeId;
            contentVersionDto.Current = !publishing;
            Database.Insert(contentVersionDto);
            entity.VersionId = contentVersionDto.Id;

            // persist the document version dto
            var documentVersionDto = dto.DocumentVersionDto;
            documentVersionDto.Id = entity.VersionId;
            if (publishing)
                documentVersionDto.Published = true;
            Database.Insert(documentVersionDto);

            // and again in case we're publishing immediately
            if (publishing)
            {
                entity.PublishedVersionId = entity.VersionId;
                contentVersionDto.Id = 0;
                contentVersionDto.Current = true;
                contentVersionDto.Text = entity.Name;
                Database.Insert(contentVersionDto);
                entity.VersionId = contentVersionDto.Id;

                documentVersionDto.Id = entity.VersionId;
                documentVersionDto.Published = false;
                Database.Insert(documentVersionDto);
            }

            // persist the property data
            IEnumerable<PropertyDataDto> propertyDataDtos = PropertyFactory.BuildDtos(entity.ContentType.Variations, entity.VersionId, entity.PublishedVersionId, entity.Properties, LanguageRepository, out var edited, out HashSet<string> editedCultures);
            foreach (PropertyDataDto propertyDataDto in propertyDataDtos)
            {
                Database.Insert(propertyDataDto);
            }

            // if !publishing, we may have a new name != current publish name,
            // also impacts 'edited'
            if (!publishing && entity.PublishName != entity.Name)
            {
                edited = true;
            }

            // persist the document dto
            // at that point, when publishing, the entity still has its old Published value
            // so we need to explicitly update the dto to persist the correct value
            if (entity.PublishedState == PublishedState.Publishing)
            {
                dto.Published = true;
            }

            dto.NodeId = nodeDto.NodeId;
            entity.Edited = dto.Edited = !dto.Published || edited; // if not published, always edited
            Database.Insert(dto);

            //insert the schedule
            PersistContentSchedule(entity, false);

            // persist the variations
            if (entity.ContentType.VariesByCulture())
            {
                // names also impact 'edited'
                // ReSharper disable once UseDeconstruction
                foreach (ContentCultureInfos cultureInfo in entity.CultureInfos)
                {
                    if (cultureInfo.Name != entity.GetPublishName(cultureInfo.Culture))
                    {
                        (editedCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(cultureInfo.Culture);
                    }
                }

                // refresh content
                entity.SetCultureEdited(editedCultures);

                // bump dates to align cultures to version
                entity.AdjustDates(contentVersionDto.VersionDate, publishing);

                // insert content variations
                Database.BulkInsertRecords(GetContentVariationDtos(entity, publishing));

                // insert document variations
                Database.BulkInsertRecords(GetDocumentVariationDtos(entity, editedCultures));
            }

            // trigger here, before we reset Published etc
            OnUowRefreshedEntity(new ContentRefreshNotification(entity, new EventMessages()));

            // flip the entity's published property
            // this also flips its published state
            // note: what depends on variations (eg PublishNames) is managed directly by the content
            if (entity.PublishedState == PublishedState.Publishing)
            {
                entity.Published = true;
                entity.PublishTemplateId = entity.TemplateId;
                entity.PublisherId = entity.WriterId;
                entity.PublishName = entity.Name;
                entity.PublishDate = entity.UpdateDate;

                SetEntityTags(entity, _tagRepository, _serializer);
            }
            else if (entity.PublishedState == PublishedState.Unpublishing)
            {
                entity.Published = false;
                entity.PublishTemplateId = null;
                entity.PublisherId = null;
                entity.PublishName = null;
                entity.PublishDate = null;

                ClearEntityTags(entity, _tagRepository);
            }

            PersistRelations(entity);

            entity.ResetDirtyProperties();

            // troubleshooting
            //if (Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.DatabaseSchema.Tables.DocumentVersion} JOIN {Constants.DatabaseSchema.Tables.ContentVersion} ON {Constants.DatabaseSchema.Tables.DocumentVersion}.id={Constants.DatabaseSchema.Tables.ContentVersion}.id WHERE published=1 AND nodeId=" + content.Id) > 1)
            //{
            //    Debugger.Break();
            //    throw new Exception("oops");
            //}
            //if (Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.DatabaseSchema.Tables.DocumentVersion} JOIN {Constants.DatabaseSchema.Tables.ContentVersion} ON {Constants.DatabaseSchema.Tables.DocumentVersion}.id={Constants.DatabaseSchema.Tables.ContentVersion}.id WHERE [current]=1 AND nodeId=" + content.Id) > 1)
            //{
            //    Debugger.Break();
            //    throw new Exception("oops");
            //}
        }

        protected override void PersistUpdatedItem(IContent entity)
        {
            var isEntityDirty = entity.IsDirty();
            var editedSnapshot = entity.Edited;

            // check if we need to make any database changes at all
            if ((entity.PublishedState == PublishedState.Published || entity.PublishedState == PublishedState.Unpublished)
                    && !isEntityDirty && !entity.IsAnyUserPropertyDirty())
            {
                return; // no change to save, do nothing, don't even update dates
            }

            // whatever we do, we must check that we are saving the current version
            var version = Database.Fetch<ContentVersionDto>(SqlContext.Sql().Select<ContentVersionDto>().From<ContentVersionDto>().Where<ContentVersionDto>(x => x.Id == entity.VersionId)).FirstOrDefault();
            if (version == null || !version.Current)
                throw new InvalidOperationException("Cannot save a non-current version.");

            // update
            entity.UpdatingEntity();

            // Check if this entity is being moved as a descendant as part of a bulk moving operations.
            // In this case we can bypass a lot of the below operations which will make this whole operation go much faster.
            // When moving we don't need to create new versions, etc... because we cannot roll this operation back anyways.
            var isMoving = entity.IsMoving();
            // TODO: I'm sure we can also detect a "Copy" (of a descendant) operation and probably perform similar checks below.
            // There is probably more stuff that would be required for copying but I'm sure not all of this logic would be, we could more than likely boost
            // copy performance by 95% just like we did for Move


            var publishing = entity.PublishedState == PublishedState.Publishing;

            if (!isMoving)
            {
                // check if we need to create a new version
                if (publishing && entity.PublishedVersionId > 0)
                {
                    // published version is not published anymore
                    Database.Execute(Sql().Update<DocumentVersionDto>(u => u.Set(x => x.Published, false)).Where<DocumentVersionDto>(x => x.Id == entity.PublishedVersionId));
                }

                // sanitize names
                SanitizeNames(entity, publishing);

                // ensure that strings don't contain characters that are invalid in xml
                // TODO: do we really want to keep doing this here?
                entity.SanitizeEntityPropertiesForXmlStorage();

                // if parent has changed, get path, level and sort order
                if (entity.IsPropertyDirty("ParentId"))
                {
                    var parent = GetParentNodeDto(entity.ParentId);
                    entity.Path = string.Concat(parent.Path, ",", entity.Id);
                    entity.Level = parent.Level + 1;
                    entity.SortOrder = GetNewChildSortOrder(entity.ParentId, 0);
                }
            }

            // create the dto
            var dto = ContentBaseFactory.BuildDto(entity, NodeObjectTypeId);

            // update the node dto
            var nodeDto = dto.ContentDto.NodeDto;
            nodeDto.ValidatePathWithException();
            Database.Update(nodeDto);

            if (!isMoving)
            {
                // update the content dto
                Database.Update(dto.ContentDto);

                // update the content & document version dtos
                var contentVersionDto = dto.DocumentVersionDto.ContentVersionDto;
                var documentVersionDto = dto.DocumentVersionDto;
                if (publishing)
                {
                    documentVersionDto.Published = true; // now published
                    contentVersionDto.Current = false; // no more current
                }
                Database.Update(contentVersionDto);
                Database.Update(documentVersionDto);

                // and, if publishing, insert new content & document version dtos
                if (publishing)
                {
                    entity.PublishedVersionId = entity.VersionId;

                    contentVersionDto.Id = 0; // want a new id
                    contentVersionDto.Current = true; // current version
                    contentVersionDto.Text = entity.Name;
                    Database.Insert(contentVersionDto);
                    entity.VersionId = documentVersionDto.Id = contentVersionDto.Id; // get the new id

                    documentVersionDto.Published = false; // non-published version
                    Database.Insert(documentVersionDto);
                }

                // replace the property data (rather than updating)
                // only need to delete for the version that existed, the new version (if any) has no property data yet
                var versionToDelete = publishing ? entity.PublishedVersionId : entity.VersionId;

                // insert property data
                ReplacePropertyValues(entity, versionToDelete, publishing ? entity.PublishedVersionId : 0, out var edited, out HashSet<string> editedCultures);

                // if !publishing, we may have a new name != current publish name,
                // also impacts 'edited'
                if (!publishing && entity.PublishName != entity.Name)
                {
                    edited = true;
                }

                // To establish the new value of "edited" we compare all properties publishedValue to editedValue and look
                // for differences.
                //
                // If we SaveAndPublish but the publish fails (e.g. already scheduled for release)
                // we have lost the publishedValue on IContent (in memory vs database) so we cannot correctly make that comparison.
                //
                // This is a slight change to behaviour, historically a publish, followed by change & save, followed by undo change & save
                // would change edited back to false.
                if (!publishing && editedSnapshot)
                {
                    edited = true;
                }

                if (entity.ContentType.VariesByCulture())
                {
                    // names also impact 'edited'
                    // ReSharper disable once UseDeconstruction
                    foreach (var cultureInfo in entity.CultureInfos)
                    {
                        if (cultureInfo.Name != entity.GetPublishName(cultureInfo.Culture))
                        {
                            edited = true;
                            (editedCultures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(cultureInfo.Culture);

                            // TODO: change tracking
                            // at the moment, we don't do any dirty tracking on property values, so we don't know whether the
                            // culture has just been edited or not, so we don't update its update date - that date only changes
                            // when the name is set, and it all works because the controller does it - but, if someone uses a
                            // service to change a property value and save (without setting name), the update date does not change.
                        }
                    }

                    // refresh content
                    entity.SetCultureEdited(editedCultures);

                    // bump dates to align cultures to version
                    entity.AdjustDates(contentVersionDto.VersionDate, publishing);

                    // replace the content version variations (rather than updating)
                    // only need to delete for the version that existed, the new version (if any) has no property data yet
                    var deleteContentVariations = Sql().Delete<ContentVersionCultureVariationDto>().Where<ContentVersionCultureVariationDto>(x => x.VersionId == versionToDelete);
                    Database.Execute(deleteContentVariations);

                    // replace the document version variations (rather than updating)
                    var deleteDocumentVariations = Sql().Delete<DocumentCultureVariationDto>().Where<DocumentCultureVariationDto>(x => x.NodeId == entity.Id);
                    Database.Execute(deleteDocumentVariations);

                    // TODO: NPoco InsertBulk issue?
                    // we should use the native NPoco InsertBulk here but it causes problems (not sure exactly all scenarios)
                    // but by using SQL Server and updating a variants name will cause: Unable to cast object of type
                    // 'Umbraco.Core.Persistence.FaultHandling.RetryDbConnection' to type 'System.Data.SqlClient.SqlConnection'.
                    // (same in PersistNewItem above)

                    // insert content variations
                    Database.BulkInsertRecords(GetContentVariationDtos(entity, publishing));

                    // insert document variations
                    Database.BulkInsertRecords(GetDocumentVariationDtos(entity, editedCultures));
                }

                // update the document dto
                // at that point, when un/publishing, the entity still has its old Published value
                // so we need to explicitly update the dto to persist the correct value
                if (entity.PublishedState == PublishedState.Publishing)
                {
                    dto.Published = true;
                }
                else if (entity.PublishedState == PublishedState.Unpublishing)
                {
                    dto.Published = false;
                }

                entity.Edited = dto.Edited = !dto.Published || edited; // if not published, always edited
                Database.Update(dto);

                //update the schedule
                if (entity.IsPropertyDirty(nameof(entity.ContentSchedule)))
                {
                    PersistContentSchedule(entity, true);
                }

                // if entity is publishing, update tags, else leave tags there
                // means that implicitly unpublished, or trashed, entities *still* have tags in db
                if (entity.PublishedState == PublishedState.Publishing)
                {
                    SetEntityTags(entity, _tagRepository, _serializer);
                }
            }

            // trigger here, before we reset Published etc
            OnUowRefreshedEntity(new ContentRefreshNotification(entity, new EventMessages()));

            if (!isMoving)
            {
                // flip the entity's published property
                // this also flips its published state
                if (entity.PublishedState == PublishedState.Publishing)
                {
                    entity.Published = true;
                    entity.PublishTemplateId = entity.TemplateId;
                    entity.PublisherId = entity.WriterId;
                    entity.PublishName = entity.Name;
                    entity.PublishDate = entity.UpdateDate;

                    SetEntityTags(entity, _tagRepository, _serializer);
                }
                else if (entity.PublishedState == PublishedState.Unpublishing)
                {
                    entity.Published = false;
                    entity.PublishTemplateId = null;
                    entity.PublisherId = null;
                    entity.PublishName = null;
                    entity.PublishDate = null;

                    ClearEntityTags(entity, _tagRepository);
                }

                PersistRelations(entity);

                // TODO: note re. tags: explicitly unpublished entities have cleared tags, but masked or trashed entities *still* have tags in the db - so what?
            }

            entity.ResetDirtyProperties();

            // troubleshooting
            //if (Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.DatabaseSchema.Tables.DocumentVersion} JOIN {Constants.DatabaseSchema.Tables.ContentVersion} ON {Constants.DatabaseSchema.Tables.DocumentVersion}.id={Constants.DatabaseSchema.Tables.ContentVersion}.id WHERE published=1 AND nodeId=" + content.Id) > 1)
            //{
            //    Debugger.Break();
            //    throw new Exception("oops");
            //}
            //if (Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {Constants.DatabaseSchema.Tables.DocumentVersion} JOIN {Constants.DatabaseSchema.Tables.ContentVersion} ON {Constants.DatabaseSchema.Tables.DocumentVersion}.id={Constants.DatabaseSchema.Tables.ContentVersion}.id WHERE [current]=1 AND nodeId=" + content.Id) > 1)
            //{
            //    Debugger.Break();
            //    throw new Exception("oops");
            //}
        }

        private void PersistContentSchedule(IContent content, bool update)
        {
            var schedules = ContentBaseFactory.BuildScheduleDto(content, LanguageRepository).ToList();

            //remove any that no longer exist
            if (update)
            {
                var ids = schedules.Where(x => x.Model.Id != Guid.Empty).Select(x => x.Model.Id).Distinct();
                Database.Execute(Sql()
                    .Delete<ContentScheduleDto>()
                    .Where<ContentScheduleDto>(x => x.NodeId == content.Id)
                    .WhereNotIn<ContentScheduleDto>(x => x.Id, ids));
            }

            //add/update the rest
            foreach (var schedule in schedules)
            {
                if (schedule.Model.Id == Guid.Empty)
                {
                    schedule.Model.Id = schedule.Dto.Id = Guid.NewGuid();
                    Database.Insert(schedule.Dto);
                }
                else
                {
                    Database.Update(schedule.Dto);
                }
            }
        }

        protected override void PersistDeletedItem(IContent entity)
        {
            // Raise event first else potential FK issues
            OnUowRemovingEntity(entity);

            //We need to clear out all access rules but we need to do this in a manual way since
            // nothing in that table is joined to a content id
            var subQuery = SqlContext.Sql()
                .Select<AccessRuleDto>(x => x.AccessId)
                .From<AccessRuleDto>()
                .InnerJoin<AccessDto>()
                .On<AccessRuleDto, AccessDto>(left => left.AccessId, right => right.Id)
                .Where<AccessDto>(dto => dto.NodeId == entity.Id);
            Database.Execute(SqlContext.SqlSyntax.GetDeleteSubquery("umbracoAccessRule", "accessId", subQuery));

            //now let the normal delete clauses take care of everything else
            base.PersistDeletedItem(entity);
        }

        #endregion

        #region Content Repository

        public int CountPublished(string contentTypeAlias = null)
        {
            var sql = SqlContext.Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.SelectCount()
                    .From<NodeDto>()
                    .InnerJoin<DocumentDto>()
                    .On<NodeDto, DocumentDto>(left => left.NodeId, right => right.NodeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId && x.Trashed == false)
                    .Where<DocumentDto>(x => x.Published);
            }
            else
            {
                sql.SelectCount()
                    .From<NodeDto>()
                    .InnerJoin<ContentDto>()
                    .On<NodeDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<DocumentDto>()
                    .On<NodeDto, DocumentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>()
                    .On<ContentTypeDto, ContentDto>(left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId && x.Trashed == false)
                    .Where<ContentTypeDto>(x => x.Alias == contentTypeAlias)
                    .Where<DocumentDto>(x => x.Published);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        public void ReplaceContentPermissions(EntityPermissionSet permissionSet)
        {
            PermissionRepository.ReplaceEntityPermissions(permissionSet);
        }

        /// <summary>
        /// Assigns a single permission to the current content item for the specified group ids
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="permission"></param>
        /// <param name="groupIds"></param>
        public void AssignEntityPermission(IContent entity, char permission, IEnumerable<int> groupIds)
        {
            PermissionRepository.AssignEntityPermission(entity, permission, groupIds);
        }

        public EntityPermissionCollection GetPermissionsForEntity(int entityId)
        {
            return PermissionRepository.GetPermissionsForEntity(entityId);
        }

        /// <summary>
        /// Used to add/update a permission for a content item
        /// </summary>
        /// <param name="permission"></param>
        public void AddOrUpdatePermissions(ContentPermissionSet permission)
        {
            PermissionRepository.Save(permission);
        }

        /// <inheritdoc />
        public override IEnumerable<IContent> GetPage(IQuery<IContent> query,
            long pageIndex, int pageSize, out long totalRecords,
            IQuery<IContent> filter, Ordering ordering)
        {
            Sql<ISqlContext> filterSql = null;

            // if we have a filter, map its clauses to an Sql statement
            if (filter != null)
            {
                // if the clause works on "name", we need to swap the field and use the variantName instead,
                // so that querying also works on variant content (for instance when searching a listview).

                // figure out how the "name" field is going to look like - so we can look for it
                var nameField = SqlContext.VisitModelField<IContent>(x => x.Name);

                filterSql = Sql();
                foreach (var filterClause in filter.GetWhereClauses())
                {
                    var clauseSql = filterClause.Item1;
                    var clauseArgs = filterClause.Item2;

                    // replace the name field
                    // we cannot reference an aliased field in a WHERE clause, so have to repeat the expression here
                    clauseSql = clauseSql.Replace(nameField, VariantNameSqlExpression);

                    // append the clause
                    filterSql.Append($"AND ({clauseSql})", clauseArgs);
                }
            }

            return GetPage<DocumentDto>(query, pageIndex, pageSize, out totalRecords,
                x => MapDtosToContent(x),
                filterSql,
                ordering);
        }

        public bool IsPathPublished(IContent content)
        {
            // fail fast
            if (content.Path.StartsWith("-1,-20,"))
                return false;

            // succeed fast
            if (content.ParentId == -1)
                return content.Published;

            var ids = content.Path.Split(Constants.CharArrays.Comma).Skip(1).Select(s => int.Parse(s, CultureInfo.InvariantCulture));

            var sql = SqlContext.Sql()
                .SelectCount<NodeDto>(x => x.NodeId)
                .From<NodeDto>()
                .InnerJoin<DocumentDto>().On<NodeDto, DocumentDto>((n, d) => n.NodeId == d.NodeId && d.Published)
                .WhereIn<NodeDto>(x => x.NodeId, ids);

            var count = Database.ExecuteScalar<int>(sql);
            return count == content.Level;
        }

        #endregion

        #region Recycle Bin

        public override int RecycleBinId => Cms.Core.Constants.System.RecycleBinContent;

        public bool RecycleBinSmells()
        {
            var cache = _appCaches.RuntimeCache;
            var cacheKey = CacheKeys.ContentRecycleBinCacheKey;

            // always cache either true or false
            return cache.GetCacheItem<bool>(cacheKey, () => CountChildren(RecycleBinId) > 0);
        }

        #endregion

        #region Read Repository implementation for Guid keys

        public IContent Get(Guid id)
        {
            return _contentByGuidReadRepository.Get(id);
        }

        IEnumerable<IContent> IReadRepository<Guid, IContent>.GetMany(params Guid[] ids)
        {
            return _contentByGuidReadRepository.GetMany(ids);
        }

        public bool Exists(Guid id)
        {
            return _contentByGuidReadRepository.Exists(id);
        }

        // reading repository purely for looking up by GUID
        // TODO: ugly and to fix we need to decouple the IRepositoryQueryable -> IRepository -> IReadRepository which should all be separate things!
        // This sub-repository pattern is super old and totally unecessary anymore, caching can be handled in much nicer ways without this
        private class ContentByGuidReadRepository : EntityRepositoryBase<Guid, IContent>
        {
            private readonly DocumentRepository _outerRepo;

            public ContentByGuidReadRepository(DocumentRepository outerRepo, IScopeAccessor scopeAccessor, AppCaches cache, ILogger<ContentByGuidReadRepository> logger)
                : base(scopeAccessor, cache, logger)
            {
                _outerRepo = outerRepo;
            }

            protected override IContent PerformGet(Guid id)
            {
                var sql = _outerRepo.GetBaseQuery(QueryType.Single)
                    .Where<NodeDto>(x => x.UniqueId == id);

                var dto = Database.Fetch<DocumentDto>(sql.SelectTop(1)).FirstOrDefault();

                if (dto == null)
                    return null;

                var content = _outerRepo.MapDtoToContent(dto);

                return content;
            }

            protected override IEnumerable<IContent> PerformGetAll(params Guid[] ids)
            {
                var sql = _outerRepo.GetBaseQuery(QueryType.Many);
                if (ids.Length > 0)
                    sql.WhereIn<NodeDto>(x => x.UniqueId, ids);

                return _outerRepo.MapDtosToContent(Database.Fetch<DocumentDto>(sql));
            }

            protected override IEnumerable<IContent> PerformGetByQuery(IQuery<IContent> query)
            {
                throw new InvalidOperationException("This method won't be implemented.");
            }

            protected override IEnumerable<string> GetDeleteClauses()
            {
                throw new InvalidOperationException("This method won't be implemented.");
            }

            protected override void PersistNewItem(IContent entity)
            {
                throw new InvalidOperationException("This method won't be implemented.");
            }

            protected override void PersistUpdatedItem(IContent entity)
            {
                throw new InvalidOperationException("This method won't be implemented.");
            }

            protected override Sql<ISqlContext> GetBaseQuery(bool isCount)
            {
                throw new InvalidOperationException("This method won't be implemented.");
            }

            protected override string GetBaseWhereClause()
            {
                throw new InvalidOperationException("This method won't be implemented.");
            }
        }

        #endregion

        #region Schedule

        /// <inheritdoc />
        public void ClearSchedule(DateTime date)
        {
            var sql = Sql().Delete<ContentScheduleDto>().Where<ContentScheduleDto>(x => x.Date <= date);
            Database.Execute(sql);
        }

        /// <inheritdoc />
        public void ClearSchedule(DateTime date, ContentScheduleAction action)
        {
            var a = action.ToString();
            var sql = Sql().Delete<ContentScheduleDto>().Where<ContentScheduleDto>(x => x.Date <= date && x.Action == a);
            Database.Execute(sql);
        }

        private Sql GetSqlForHasScheduling(ContentScheduleAction action, DateTime date)
        {
            var template = SqlContext.Templates.Get("Umbraco.Core.DocumentRepository.GetSqlForHasScheduling", tsql => tsql
                .SelectCount()
                    .From<ContentScheduleDto>()
                    .Where<ContentScheduleDto>(x => x.Action == SqlTemplate.Arg<string>("action") && x.Date <= SqlTemplate.Arg<DateTime>("date")));

            var sql = template.Sql(action.ToString(), date);
            return sql;
        }

        public bool HasContentForExpiration(DateTime date)
        {
            var sql = GetSqlForHasScheduling(ContentScheduleAction.Expire, date);
            return Database.ExecuteScalar<int>(sql) > 0;
        }

        public bool HasContentForRelease(DateTime date)
        {
            var sql = GetSqlForHasScheduling(ContentScheduleAction.Release, date);
            return Database.ExecuteScalar<int>(sql) > 0;
        }

        /// <inheritdoc />
        public IEnumerable<IContent> GetContentForRelease(DateTime date)
        {
            var action = ContentScheduleAction.Release.ToString();

            var sql = GetBaseQuery(QueryType.Many)
                .WhereIn<NodeDto>(x => x.NodeId, Sql()
                    .Select<ContentScheduleDto>(x => x.NodeId)
                    .From<ContentScheduleDto>()
                    .Where<ContentScheduleDto>(x => x.Action == action && x.Date <= date));

            AddGetByQueryOrderBy(sql);

            return MapDtosToContent(Database.Fetch<DocumentDto>(sql));
        }

        /// <inheritdoc />
        public IEnumerable<IContent> GetContentForExpiration(DateTime date)
        {
            var action = ContentScheduleAction.Expire.ToString();

            var sql = GetBaseQuery(QueryType.Many)
                .WhereIn<NodeDto>(x => x.NodeId, Sql()
                    .Select<ContentScheduleDto>(x => x.NodeId)
                    .From<ContentScheduleDto>()
                    .Where<ContentScheduleDto>(x => x.Action == action && x.Date <= date));

            AddGetByQueryOrderBy(sql);

            return MapDtosToContent(Database.Fetch<DocumentDto>(sql));
        }

        #endregion

        protected override string ApplySystemOrdering(ref Sql<ISqlContext> sql, Ordering ordering)
        {
            // note: 'updater' is the user who created the latest draft version,
            //       we don't have an 'updater' per culture (should we?)
            if (ordering.OrderBy.InvariantEquals("updater"))
            {
                var joins = Sql()
                    .InnerJoin<UserDto>("updaterUser").On<ContentVersionDto, UserDto>((version, user) => version.UserId == user.Id, aliasRight: "updaterUser");

                // see notes in ApplyOrdering: the field MUST be selected + aliased
                sql = Sql(InsertBefore(sql, "FROM", ", " + SqlSyntax.GetFieldName<UserDto>(x => x.UserName, "updaterUser") + " AS ordering "), sql.Arguments);

                sql = InsertJoins(sql, joins);

                return "ordering";
            }

            if (ordering.OrderBy.InvariantEquals("published"))
            {
                // no culture = can only work on the global 'published' flag
                if (ordering.Culture.IsNullOrWhiteSpace())
                {
                    // see notes in ApplyOrdering: the field MUST be selected + aliased, and we cannot have
                    // the whole CASE fragment in ORDER BY due to it not being detected by NPoco
                    sql = Sql(InsertBefore(sql, "FROM", ", (CASE WHEN pcv.id IS NULL THEN 0 ELSE 1 END) AS ordering "), sql.Arguments);
                    return "ordering";
                }

                // invariant: left join will yield NULL and we must use pcv to determine published
                // variant: left join may yield NULL or something, and that determines published


                var joins = Sql()
                    .InnerJoin<ContentTypeDto>("ctype").On<ContentDto, ContentTypeDto>((content, contentType) => content.ContentTypeId == contentType.NodeId, aliasRight: "ctype")
                    // left join on optional culture variation
                    //the magic "[[[ISOCODE]]]" parameter value will be replaced in ContentRepositoryBase.GetPage() by the actual ISO code
                    .LeftJoin<ContentVersionCultureVariationDto>(nested =>
                        nested.InnerJoin<LanguageDto>("langp").On<ContentVersionCultureVariationDto, LanguageDto>((ccv, lang) => ccv.LanguageId == lang.Id && lang.IsoCode == "[[[ISOCODE]]]", "ccvp", "langp"), "ccvp")
                    .On<ContentVersionDto, ContentVersionCultureVariationDto>((version, ccv) => version.Id == ccv.VersionId, aliasLeft: "pcv", aliasRight: "ccvp");

                sql = InsertJoins(sql, joins);

                // see notes in ApplyOrdering: the field MUST be selected + aliased, and we cannot have
                // the whole CASE fragment in ORDER BY due to it not being detected by NPoco
                var sqlText = InsertBefore(sql.SQL, "FROM",

                    // when invariant, ie 'variations' does not have the culture flag (value 1), use the global 'published' flag on pcv.id,
                    // otherwise check if there's a version culture variation for the lang, via ccv.id
                    ", (CASE WHEN (ctype.variations & 1) = 0 THEN (CASE WHEN pcv.id IS NULL THEN 0 ELSE 1 END) ELSE (CASE WHEN ccvp.id IS NULL THEN 0 ELSE 1 END) END) AS ordering "); // trailing space is important!

                sql = Sql(sqlText, sql.Arguments);

                return "ordering";
            }

            return base.ApplySystemOrdering(ref sql, ordering);
        }

        private IEnumerable<IContent> MapDtosToContent(List<DocumentDto> dtos,
            bool withCache = false,
            bool loadProperties = true,
            bool loadTemplates = true,
            bool loadSchedule = true,
            bool loadVariants = true)
        {
            var temps = new List<TempContent<Content>>();
            var contentTypes = new Dictionary<int, IContentType>();
            var templateIds = new List<int>();

            var content = new Content[dtos.Count];

            for (var i = 0; i < dtos.Count; i++)
            {
                var dto = dtos[i];

                if (withCache)
                {
                    // if the cache contains the (proper version of the) item, use it
                    var cached = IsolatedCache.GetCacheItem<IContent>(RepositoryCacheKeys.GetKey<IContent, int>(dto.NodeId));
                    if (cached != null && cached.VersionId == dto.DocumentVersionDto.ContentVersionDto.Id)
                    {
                        content[i] = (Content)cached;
                        continue;
                    }
                }

                // else, need to build it

                // get the content type - the repository is full cache *but* still deep-clones
                // whatever comes out of it, so use our own local index here to avoid this
                var contentTypeId = dto.ContentDto.ContentTypeId;
                if (contentTypes.TryGetValue(contentTypeId, out var contentType) == false)
                    contentTypes[contentTypeId] = contentType = _contentTypeRepository.Get(contentTypeId);

                var c = content[i] = ContentBaseFactory.BuildEntity(dto, contentType);

                if (loadTemplates)
                {
                    // need templates
                    var templateId = dto.DocumentVersionDto.TemplateId;
                    if (templateId.HasValue && templateId.Value > 0)
                        templateIds.Add(templateId.Value);
                    if (dto.Published)
                    {
                        templateId = dto.PublishedVersionDto.TemplateId;
                        if (templateId.HasValue && templateId.Value > 0)
                            templateIds.Add(templateId.Value);
                    }
                }

                // need temps, for properties, templates and variations
                var versionId = dto.DocumentVersionDto.Id;
                var publishedVersionId = dto.Published ? dto.PublishedVersionDto.Id : 0;
                var temp = new TempContent<Content>(dto.NodeId, versionId, publishedVersionId, contentType, c)
                {
                    Template1Id = dto.DocumentVersionDto.TemplateId
                };
                if (dto.Published)
                    temp.Template2Id = dto.PublishedVersionDto.TemplateId;
                temps.Add(temp);
            }

            Dictionary<int, ITemplate> templates = null;
            if (loadTemplates)
            {
                // load all required templates in 1 query, and index
                templates = _templateRepository.GetMany(templateIds.ToArray())
                    .ToDictionary(x => x.Id, x => x);
            }

            IDictionary<int, PropertyCollection> properties = null;
            if (loadProperties)
            {
                // load all properties for all documents from database in 1 query - indexed by version id
                properties = GetPropertyCollections(temps);
            }

            var schedule = GetContentSchedule(temps.Select(x => x.Content.Id).ToArray());

            // assign templates and properties
            foreach (var temp in temps)
            {
                if (loadTemplates)
                {
                    // set the template ID if it matches an existing template
                    if (temp.Template1Id.HasValue && templates.ContainsKey(temp.Template1Id.Value))
                        temp.Content.TemplateId = temp.Template1Id;
                    if (temp.Template2Id.HasValue && templates.ContainsKey(temp.Template2Id.Value))
                        temp.Content.PublishTemplateId = temp.Template2Id;
                }


                // set properties
                if (loadProperties)
                {
                    if (properties.ContainsKey(temp.VersionId))
                        temp.Content.Properties = properties[temp.VersionId];
                    else
                        throw new InvalidOperationException($"No property data found for version: '{temp.VersionId}'.");
                }

                if (loadSchedule)
                {
                    // load in the schedule
                    if (schedule.TryGetValue(temp.Content.Id, out var s))
                        temp.Content.ContentSchedule = s;
                }

            }

            if (loadVariants)
            {
                // set variations, if varying
                temps = temps.Where(x => x.ContentType.VariesByCulture()).ToList();
                if (temps.Count > 0)
                {
                    // load all variations for all documents from database, in one query
                    var contentVariations = GetContentVariations(temps);
                    var documentVariations = GetDocumentVariations(temps);
                    foreach (var temp in temps)
                        SetVariations(temp.Content, contentVariations, documentVariations);
                }
            }



            foreach (var c in content)
                c.ResetDirtyProperties(false); // reset dirty initial properties (U4-1946)

            return content;
        }

        private IContent MapDtoToContent(DocumentDto dto)
        {
            var contentType = _contentTypeRepository.Get(dto.ContentDto.ContentTypeId);
            var content = ContentBaseFactory.BuildEntity(dto, contentType);

            try
            {
                content.DisableChangeTracking();

                // get template
                if (dto.DocumentVersionDto.TemplateId.HasValue && dto.DocumentVersionDto.TemplateId.Value > 0)
                    content.TemplateId = dto.DocumentVersionDto.TemplateId;

                // get properties - indexed by version id
                var versionId = dto.DocumentVersionDto.Id;

                // TODO: shall we get published properties or not?
                //var publishedVersionId = dto.Published ? dto.PublishedVersionDto.Id : 0;
                var publishedVersionId = dto.PublishedVersionDto?.Id ?? 0;

                var temp = new TempContent<Content>(dto.NodeId, versionId, publishedVersionId, contentType);
                var ltemp = new List<TempContent<Content>> { temp };
                var properties = GetPropertyCollections(ltemp);
                content.Properties = properties[dto.DocumentVersionDto.Id];

                // set variations, if varying
                if (contentType.VariesByCulture())
                {
                    var contentVariations = GetContentVariations(ltemp);
                    var documentVariations = GetDocumentVariations(ltemp);
                    SetVariations(content, contentVariations, documentVariations);
                }

                //load in the schedule
                var schedule = GetContentSchedule(dto.NodeId);
                if (schedule.TryGetValue(dto.NodeId, out var s))
                    content.ContentSchedule = s;

                // reset dirty initial properties (U4-1946)
                content.ResetDirtyProperties(false);
                return content;
            }
            finally
            {
                content.EnableChangeTracking();
            }
        }

        private IDictionary<int, ContentScheduleCollection> GetContentSchedule(params int[] contentIds)
        {
            var result = new Dictionary<int, ContentScheduleCollection>();

            var scheduleDtos = Database.FetchByGroups<ContentScheduleDto, int>(contentIds, 2000, batch => Sql()
                .Select<ContentScheduleDto>()
                .From<ContentScheduleDto>()
                .WhereIn<ContentScheduleDto>(x => x.NodeId, batch));

            foreach (var scheduleDto in scheduleDtos)
            {
                if (!result.TryGetValue(scheduleDto.NodeId, out var col))
                    col = result[scheduleDto.NodeId] = new ContentScheduleCollection();

                col.Add(new ContentSchedule(scheduleDto.Id,
                    LanguageRepository.GetIsoCodeById(scheduleDto.LanguageId) ?? string.Empty,
                    scheduleDto.Date,
                    scheduleDto.Action == ContentScheduleAction.Release.ToString()
                        ? ContentScheduleAction.Release
                        : ContentScheduleAction.Expire));
            }

            return result;
        }

        private void SetVariations(Content content, IDictionary<int, List<ContentVariation>> contentVariations, IDictionary<int, List<DocumentVariation>> documentVariations)
        {
            if (contentVariations.TryGetValue(content.VersionId, out var contentVariation))
                foreach (var v in contentVariation)
                    content.SetCultureInfo(v.Culture, v.Name, v.Date);

            if (content.PublishedVersionId > 0 && contentVariations.TryGetValue(content.PublishedVersionId, out contentVariation))
            {
                foreach (var v in contentVariation)
                    content.SetPublishInfo(v.Culture, v.Name, v.Date);
            }

            if (documentVariations.TryGetValue(content.Id, out var documentVariation))
                content.SetCultureEdited(documentVariation.Where(x => x.Edited).Select(x => x.Culture));
        }

        private IDictionary<int, List<ContentVariation>> GetContentVariations<T>(List<TempContent<T>> temps)
            where T : class, IContentBase
        {
            var versions = new List<int>();
            foreach (var temp in temps)
            {
                versions.Add(temp.VersionId);
                if (temp.PublishedVersionId > 0)
                    versions.Add(temp.PublishedVersionId);
            }
            if (versions.Count == 0)
                return new Dictionary<int, List<ContentVariation>>();

            var dtos = Database.FetchByGroups<ContentVersionCultureVariationDto, int>(versions, 2000, batch
                => Sql()
                    .Select<ContentVersionCultureVariationDto>()
                    .From<ContentVersionCultureVariationDto>()
                    .WhereIn<ContentVersionCultureVariationDto>(x => x.VersionId, batch));

            var variations = new Dictionary<int, List<ContentVariation>>();

            foreach (var dto in dtos)
            {
                if (!variations.TryGetValue(dto.VersionId, out var variation))
                    variations[dto.VersionId] = variation = new List<ContentVariation>();

                variation.Add(new ContentVariation
                {
                    Culture = LanguageRepository.GetIsoCodeById(dto.LanguageId),
                    Name = dto.Name,
                    Date = dto.UpdateDate
                });
            }

            return variations;
        }

        private IDictionary<int, List<DocumentVariation>> GetDocumentVariations<T>(List<TempContent<T>> temps)
            where T : class, IContentBase
        {
            var ids = temps.Select(x => x.Id);

            var dtos = Database.FetchByGroups<DocumentCultureVariationDto, int>(ids, 2000, batch =>
                Sql()
                    .Select<DocumentCultureVariationDto>()
                    .From<DocumentCultureVariationDto>()
                    .WhereIn<DocumentCultureVariationDto>(x => x.NodeId, batch));

            var variations = new Dictionary<int, List<DocumentVariation>>();

            foreach (var dto in dtos)
            {
                if (!variations.TryGetValue(dto.NodeId, out var variation))
                    variations[dto.NodeId] = variation = new List<DocumentVariation>();

                variation.Add(new DocumentVariation
                {
                    Culture = LanguageRepository.GetIsoCodeById(dto.LanguageId),
                    Edited = dto.Edited
                });
            }

            return variations;
        }

        private IEnumerable<ContentVersionCultureVariationDto> GetContentVariationDtos(IContent content, bool publishing)
        {
            // create dtos for the 'current' (non-published) version, all cultures
            // ReSharper disable once UseDeconstruction
            foreach (var cultureInfo in content.CultureInfos)
                yield return new ContentVersionCultureVariationDto
                {
                    VersionId = content.VersionId,
                    LanguageId = LanguageRepository.GetIdByIsoCode(cultureInfo.Culture) ?? throw new InvalidOperationException("Not a valid culture."),
                    Culture = cultureInfo.Culture,
                    Name = cultureInfo.Name,
                    UpdateDate = content.GetUpdateDate(cultureInfo.Culture) ?? DateTime.MinValue // we *know* there is a value
                };

            // if not publishing, we're just updating the 'current' (non-published) version,
            // so there are no DTOs to create for the 'published' version which remains unchanged
            if (!publishing)
                yield break;

            // create dtos for the 'published' version, for published cultures (those having a name)
            // ReSharper disable once UseDeconstruction
            foreach (var cultureInfo in content.PublishCultureInfos)
                yield return new ContentVersionCultureVariationDto
                {
                    VersionId = content.PublishedVersionId,
                    LanguageId = LanguageRepository.GetIdByIsoCode(cultureInfo.Culture) ?? throw new InvalidOperationException("Not a valid culture."),
                    Culture = cultureInfo.Culture,
                    Name = cultureInfo.Name,
                    UpdateDate = content.GetPublishDate(cultureInfo.Culture) ?? DateTime.MinValue // we *know* there is a value
                };
        }

        private IEnumerable<DocumentCultureVariationDto> GetDocumentVariationDtos(IContent content, HashSet<string> editedCultures)
        {
            var allCultures = content.AvailableCultures.Union(content.PublishedCultures); // union = distinct
            foreach (var culture in allCultures)
            {
                var dto = new DocumentCultureVariationDto
                {
                    NodeId = content.Id,
                    LanguageId = LanguageRepository.GetIdByIsoCode(culture) ?? throw new InvalidOperationException("Not a valid culture."),
                    Culture = culture,

                    Name = content.GetCultureName(culture) ?? content.GetPublishName(culture),
                    Available = content.IsCultureAvailable(culture),
                    Published = content.IsCulturePublished(culture),
                    // note: can't use IsCultureEdited at that point - hasn't been updated yet - see PersistUpdatedItem
                    Edited = content.IsCultureAvailable(culture) &&
                             (!content.IsCulturePublished(culture) || (editedCultures != null && editedCultures.Contains(culture)))
                };

                yield return dto;
            }

        }

        private class ContentVariation
        {
            public string Culture { get; set; }
            public string Name { get; set; }
            public DateTime Date { get; set; }
        }

        private class DocumentVariation
        {
            public string Culture { get; set; }
            public bool Edited { get; set; }
        }

        #region Utilities

        private void SanitizeNames(IContent content, bool publishing)
        {
            // a content item *must* have an invariant name, and invariant published name
            // else we just cannot write the invariant rows (node, content version...) to the database

            // ensure that we have an invariant name
            // invariant content = must be there already, else throw
            // variant content = update with default culture or anything really
            EnsureInvariantNameExists(content);

            // ensure that invariant name is unique
            EnsureInvariantNameIsUnique(content);

            // and finally,
            // ensure that each culture has a unique node name
            // no published name = not published
            // else, it needs to be unique
            EnsureVariantNamesAreUnique(content, publishing);
        }

        private void EnsureInvariantNameExists(IContent content)
        {
            if (content.ContentType.VariesByCulture())
            {
                // content varies by culture
                // then it must have at least a variant name, else it makes no sense
                if (content.CultureInfos.Count == 0)
                    throw new InvalidOperationException("Cannot save content with an empty name.");

                // and then, we need to set the invariant name implicitly,
                // using the default culture if it has a name, otherwise anything we can
                var defaultCulture = LanguageRepository.GetDefaultIsoCode();
                content.Name = defaultCulture != null && content.CultureInfos.TryGetValue(defaultCulture, out var cultureName)
                    ? cultureName.Name
                    : content.CultureInfos[0].Name;
            }
            else
            {
                // content is invariant, and invariant content must have an explicit invariant name
                if (string.IsNullOrWhiteSpace(content.Name))
                    throw new InvalidOperationException("Cannot save content with an empty name.");
            }
        }

        private void EnsureInvariantNameIsUnique(IContent content)
        {
            content.Name = EnsureUniqueNodeName(content.ParentId, content.Name, content.Id);
        }

        protected override string EnsureUniqueNodeName(int parentId, string nodeName, int id = 0)
        {
            return EnsureUniqueNaming == false ? nodeName : base.EnsureUniqueNodeName(parentId, nodeName, id);
        }

        private SqlTemplate SqlEnsureVariantNamesAreUnique => SqlContext.Templates.Get("Umbraco.Core.DomainRepository.EnsureVariantNamesAreUnique", tsql => tsql
            .Select<ContentVersionCultureVariationDto>(x => x.Id, x => x.Name, x => x.LanguageId)
            .From<ContentVersionCultureVariationDto>()
            .InnerJoin<ContentVersionDto>().On<ContentVersionDto, ContentVersionCultureVariationDto>(x => x.Id, x => x.VersionId)
            .InnerJoin<NodeDto>().On<NodeDto, ContentVersionDto>(x => x.NodeId, x => x.NodeId)
            .Where<ContentVersionDto>(x => x.Current == SqlTemplate.Arg<bool>("current"))
            .Where<NodeDto>(x => x.NodeObjectType == SqlTemplate.Arg<Guid>("nodeObjectType") &&
                                 x.ParentId == SqlTemplate.Arg<int>("parentId") &&
                                 x.NodeId != SqlTemplate.Arg<int>("id"))
            .OrderBy<ContentVersionCultureVariationDto>(x => x.LanguageId));

        private void EnsureVariantNamesAreUnique(IContent content, bool publishing)
        {
            if (!EnsureUniqueNaming || !content.ContentType.VariesByCulture() || content.CultureInfos.Count == 0)
                return;

            // get names per culture, at same level (ie all siblings)
            var sql = SqlEnsureVariantNamesAreUnique.Sql(true, NodeObjectTypeId, content.ParentId, content.Id);
            var names = Database.Fetch<CultureNodeName>(sql)
                .GroupBy(x => x.LanguageId)
                .ToDictionary(x => x.Key, x => x);

            if (names.Count == 0)
                return;

            // note: the code below means we are going to unique-ify every culture names, regardless
            // of whether the name has changed (ie the culture has been updated) - some saving culture
            // fr-FR could cause culture en-UK name to change - not sure that is clean

            foreach (var cultureInfo in content.CultureInfos)
            {
                var langId = LanguageRepository.GetIdByIsoCode(cultureInfo.Culture);
                if (!langId.HasValue)
                    continue;
                if (!names.TryGetValue(langId.Value, out var cultureNames))
                    continue;

                // get a unique name
                var otherNames = cultureNames.Select(x => new SimilarNodeName { Id = x.Id, Name = x.Name });
                var uniqueName = SimilarNodeName.GetUniqueName(otherNames, 0, cultureInfo.Name);

                if (uniqueName == content.GetCultureName(cultureInfo.Culture))
                    continue;

                // update the name, and the publish name if published
                content.SetCultureName(uniqueName, cultureInfo.Culture);
                if (publishing && content.PublishCultureInfos.ContainsKey(cultureInfo.Culture))
                    content.SetPublishInfo(cultureInfo.Culture, uniqueName, DateTime.Now); //TODO: This is weird, this call will have already been made in the SetCultureName
            }
        }

        // ReSharper disable once ClassNeverInstantiated.Local
        private class CultureNodeName
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int LanguageId { get; set; }
        }

        #endregion
    }
}
