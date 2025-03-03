using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Configuration.Models;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Hosting;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.PublishedCache;
using Umbraco.Cms.Infrastructure.PublishedCache.DataSource;
using Umbraco.Cms.Infrastructure.Serialization;
using Umbraco.Cms.Tests.Common;
using Umbraco.Extensions;
using Umbraco.Tests.TestHelpers;
using Umbraco.Tests.Testing.Objects;
using Umbraco.Web.Composing;

namespace Umbraco.Tests.PublishedContent
{
    [TestFixture]
    public class NuCacheChildrenTests
    {
        private IPublishedModelFactory PublishedModelFactory { get; } = new NoopPublishedModelFactory();
        private IVariationContextAccessor VariationContextAccessor  { get; } = TestHelper.VariationContextAccessor;

        private IPublishedSnapshotService _snapshotService;
        private IVariationContextAccessor _variationAccesor;
        private IPublishedSnapshotAccessor _snapshotAccessor;
        private ContentType _contentTypeInvariant;
        private ContentType _contentTypeVariant;
        private TestDataSource _source;
        private IContentCacheDataSerializerFactory _contentNestedDataSerializerFactory;

        [TearDown]
        public void Teardown()
        {
            _snapshotService?.Dispose();
        }

        private void Init(Func<IEnumerable<ContentNodeKit>> kits)
        {
            var factory = Mock.Of<IServiceProvider>();
            Current.Factory = factory;

            var hostingEnvironment = Mock.Of<IHostingEnvironment>();

            Mock.Get(factory).Setup(x => x.GetService(typeof(IPublishedModelFactory))).Returns(PublishedModelFactory);

            var runtime = Mock.Of<IRuntimeState>();
            Mock.Get(runtime).Setup(x => x.Level).Returns(RuntimeLevel.Run);

            var serializer = new ConfigurationEditorJsonSerializer();

            // create data types, property types and content types
            var dataType = new DataType(new VoidEditor("Editor", Mock.Of<IDataValueEditorFactory>()), serializer) { Id = 3 };

            var dataTypes = new[]
            {
                dataType
            };

            var propertyType = new PropertyType(TestHelper.ShortStringHelper, "Umbraco.Void.Editor", ValueStorageType.Nvarchar) { Alias = "prop", DataTypeId = 3, Variations = ContentVariation.Nothing };
            _contentTypeInvariant = new ContentType(TestHelper.ShortStringHelper, -1) { Id = 2, Alias = "itype", Variations = ContentVariation.Nothing };
            _contentTypeInvariant.AddPropertyType(propertyType);

            propertyType = new PropertyType(TestHelper.ShortStringHelper, "Umbraco.Void.Editor", ValueStorageType.Nvarchar) { Alias = "prop", DataTypeId = 3, Variations = ContentVariation.Culture };
            _contentTypeVariant = new ContentType(TestHelper.ShortStringHelper, -1) { Id = 3, Alias = "vtype", Variations = ContentVariation.Culture };
            _contentTypeVariant.AddPropertyType(propertyType);

            var contentTypes = new[]
            {
                _contentTypeInvariant,
                _contentTypeVariant
            };

            var contentTypeService = new Mock<IContentTypeService>();
            contentTypeService.Setup(x => x.GetAll()).Returns(contentTypes);
            contentTypeService.Setup(x => x.GetAll(It.IsAny<int[]>())).Returns(contentTypes);

            var mediaTypeService = new Mock<IMediaTypeService>();
            mediaTypeService.Setup(x => x.GetAll()).Returns(Enumerable.Empty<IMediaType>());
            mediaTypeService.Setup(x => x.GetAll(It.IsAny<int[]>())).Returns(Enumerable.Empty<IMediaType>());

            var contentTypeServiceBaseFactory = new Mock<IContentTypeBaseServiceProvider>();
            contentTypeServiceBaseFactory.Setup(x => x.For(It.IsAny<IContentBase>())).Returns(contentTypeService.Object);

            var dataTypeService = Mock.Of<IDataTypeService>();
            Mock.Get(dataTypeService).Setup(x => x.GetAll()).Returns(dataTypes);

            // create a service context
            var serviceContext = ServiceContext.CreatePartial(
                dataTypeService: dataTypeService,
                memberTypeService: Mock.Of<IMemberTypeService>(),
                memberService: Mock.Of<IMemberService>(),
                contentTypeService: contentTypeService.Object,
                mediaTypeService: mediaTypeService.Object,
                localizationService: Mock.Of<ILocalizationService>(),
                domainService: Mock.Of<IDomainService>()
            );

            // create a scope provider
            var scopeProvider = new Mock<IScopeProvider>();
            scopeProvider
                .Setup(x => x.CreateScope(
                    It.IsAny<IsolationLevel>(),
                    It.IsAny<RepositoryCacheMode>(),
                    It.IsAny<IEventDispatcher>(),
                    It.IsAny<bool?>(),
                    It.IsAny<bool>(),
                    It.IsAny<bool>()))
                .Returns(Mock.Of<IScope>);

            // create a published content type factory
            var contentTypeFactory = new PublishedContentTypeFactory(
                PublishedModelFactory,
                new PropertyValueConverterCollection(Array.Empty<IPropertyValueConverter>()),
                dataTypeService);

            // create accessors
            _variationAccesor = new TestVariationContextAccessor();
            _snapshotAccessor = new TestPublishedSnapshotAccessor();

            // create a data source for NuCache
            _source = new TestDataSource(kits());
            _contentNestedDataSerializerFactory = new JsonContentNestedDataSerializerFactory();

            var typeFinder = TestHelper.GetTypeFinder();

            var globalSettings = new GlobalSettings();
            var nuCacheSettings = new NuCacheSettings();
            
            // at last, create the complete NuCache snapshot service!
            var options = new PublishedSnapshotServiceOptions { IgnoreLocalDb = true };
            _snapshotService = new PublishedSnapshotService(
                options,
                null,
                serviceContext,
                contentTypeFactory,
                _snapshotAccessor,
                _variationAccesor,
                Mock.Of<IProfilingLogger>(),
                NullLoggerFactory.Instance,
                scopeProvider.Object,
                _source,
                new TestDefaultCultureAccessor(),
                Options.Create(globalSettings),
                Mock.Of<IEntityXmlSerializer>(),
                PublishedModelFactory,
                hostingEnvironment,
                Options.Create(nuCacheSettings),
                _contentNestedDataSerializerFactory);
                

            // invariant is the current default
            _variationAccesor.VariationContext = new VariationContext();

            Mock.Get(factory).Setup(x => x.GetService(typeof(IVariationContextAccessor))).Returns(_variationAccesor);
        }

        private IEnumerable<ContentNodeKit> GetNestedVariantKits()
        {
            var paths = new Dictionary<int, string> { { -1, "-1" } };

            //1x variant (root)
            yield return CreateVariantKit(1, -1, 1, paths);

            //1x invariant under root
            yield return CreateInvariantKit(4, 1, 1, paths);

            //1x variant under root
            yield return CreateVariantKit(7, 1, 4, paths);

            //2x mixed under invariant
            yield return CreateVariantKit(10, 4, 1, paths);
            yield return CreateInvariantKit(11, 4, 2, paths);

            //2x mixed under variant
            yield return CreateVariantKit(12, 7, 1, paths);
            yield return CreateInvariantKit(13, 7, 2, paths);
        }

        private IEnumerable<ContentNodeKit> GetInvariantKits()
        {
            var paths = new Dictionary<int, string> { { -1, "-1" } };

            yield return CreateInvariantKit(1, -1, 1, paths);
            yield return CreateInvariantKit(2, -1, 2, paths);
            yield return CreateInvariantKit(3, -1, 3, paths);

            yield return CreateInvariantKit(4, 1, 1, paths);
            yield return CreateInvariantKit(5, 1, 2, paths);
            yield return CreateInvariantKit(6, 1, 3, paths);

            yield return CreateInvariantKit(7, 2, 3, paths);
            yield return CreateInvariantKit(8, 2, 2, paths);
            yield return CreateInvariantKit(9, 2, 1, paths);

            yield return CreateInvariantKit(10, 3, 1, paths);

            yield return CreateInvariantKit(11, 4, 1, paths);
            yield return CreateInvariantKit(12, 4, 2, paths);
        }

        private ContentNodeKit CreateInvariantKit(int id, int parentId, int sortOrder, Dictionary<int, string> paths)
        {
            if (!paths.TryGetValue(parentId, out var parentPath))
                throw new Exception("Unknown parent.");

            var path = paths[id] = parentPath + "," + id;
            var level = path.Count(x => x == ',');
            var now = DateTime.Now;

            var contentData = new ContentData
            {
                Name = "N" + id,
                Published = true,
                TemplateId = 0,
                VersionId = 1,
                VersionDate = now,
                WriterId = 0,
                Properties = new Dictionary<string, PropertyData[]>(),
                CultureInfos = new Dictionary<string, CultureVariation>()
            };

            return new ContentNodeKit
            {
                ContentTypeId = _contentTypeInvariant.Id,
                Node = new ContentNode(id, Guid.NewGuid(), level, path, sortOrder, parentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = contentData
            };
        }

        private IEnumerable<ContentNodeKit> GetVariantKits()
        {
            var paths = new Dictionary<int, string> { { -1, "-1" } };

            yield return CreateVariantKit(1, -1, 1, paths);
            yield return CreateVariantKit(2, -1, 2, paths);
            yield return CreateVariantKit(3, -1, 3, paths);

            yield return CreateVariantKit(4, 1, 1, paths);
            yield return CreateVariantKit(5, 1, 2, paths);
            yield return CreateVariantKit(6, 1, 3, paths);

            yield return CreateVariantKit(7, 2, 3, paths);
            yield return CreateVariantKit(8, 2, 2, paths);
            yield return CreateVariantKit(9, 2, 1, paths);

            yield return CreateVariantKit(10, 3, 1, paths);

            yield return CreateVariantKit(11, 4, 1, paths);
            yield return CreateVariantKit(12, 4, 2, paths);
        }

        private static Dictionary<string, CultureVariation> GetCultureInfos(int id, DateTime now)
        {
            var en = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            var fr = new[] { 1, 3, 4, 6, 7, 9, 10, 12 };

            var infos = new Dictionary<string, CultureVariation>();
            if (en.Contains(id))
                infos["en-US"] = new CultureVariation { Name = "N" + id + "-" + "en-US", Date = now, IsDraft = false };
            if (fr.Contains(id))
                infos["fr-FR"] = new CultureVariation { Name = "N" + id + "-" + "fr-FR", Date = now, IsDraft = false };
            return infos;
        }

        private ContentNodeKit CreateVariantKit(int id, int parentId, int sortOrder, Dictionary<int, string> paths)
        {
            if (!paths.TryGetValue(parentId, out var parentPath))
                throw new Exception("Unknown parent.");

            var path = paths[id] = parentPath + "," + id;
            var level = path.Count(x => x == ',');
            var now = DateTime.Now;

            return new ContentNodeKit
            {
                ContentTypeId = _contentTypeVariant.Id,
                Node = new ContentNode(id, Guid.NewGuid(), level, path, sortOrder, parentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = "N" + id,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = GetCultureInfos(id, now)
                }
            };
        }

        private IEnumerable<ContentNodeKit> GetVariantWithDraftKits()
        {
            var paths = new Dictionary<int, string> { { -1, "-1" } };

            Dictionary<string, CultureVariation> GetCultureInfos(int id, DateTime now)
            {
                var en = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
                var fr = new[] { 1, 3, 4, 6, 7, 9, 10, 12 };

                var infos = new Dictionary<string, CultureVariation>();
                if (en.Contains(id))
                    infos["en-US"] = new CultureVariation { Name = "N" + id + "-" + "en-US", Date = now, IsDraft = false };
                if (fr.Contains(id))
                    infos["fr-FR"] = new CultureVariation { Name = "N" + id + "-" + "fr-FR", Date = now, IsDraft = false };
                return infos;
            }

            ContentNodeKit CreateKit(int id, int parentId, int sortOrder)
            {
                if (!paths.TryGetValue(parentId, out var parentPath))
                    throw new Exception("Unknown parent.");

                var path = paths[id] = parentPath + "," + id;
                var level = path.Count(x => x == ',');
                var now = DateTime.Now;

                ContentData CreateContentData(bool published) => new ContentData
                {
                    Name = "N" + id,
                    Published = published,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = GetCultureInfos(id, now)
                };

                var withDraft = id % 2 == 0;
                var withPublished = !withDraft;

                return new ContentNodeKit
                {
                    ContentTypeId = _contentTypeVariant.Id,
                    Node = new ContentNode(id, Guid.NewGuid(), level, path, sortOrder, parentId, DateTime.Now, 0),
                    DraftData = withDraft ? CreateContentData(false) : null,
                    PublishedData = withPublished ? CreateContentData(true) : null
                };
            }

            yield return CreateKit(1, -1, 1);
            yield return CreateKit(2, -1, 2);
            yield return CreateKit(3, -1, 3);

            yield return CreateKit(4, 1, 1);
            yield return CreateKit(5, 1, 2);
            yield return CreateKit(6, 1, 3);

            yield return CreateKit(7, 2, 3);
            yield return CreateKit(8, 2, 2);
            yield return CreateKit(9, 2, 1);

            yield return CreateKit(10, 3, 1);

            yield return CreateKit(11, 4, 1);
            yield return CreateKit(12, 4, 2);
        }

        [Test]
        public void EmptyTest()
        {
            Init(() => Enumerable.Empty<ContentNodeKit>());

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            var documents = snapshot.Content.GetAtRoot().ToArray();
            Assert.AreEqual(0, documents.Length);
        }

        [Test]
        public void ChildrenTest()
        {
            Init(GetInvariantKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1", "N2", "N3");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N5", "N6");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9", "N8", "N7");

            documents = snapshot.Content.GetById(3).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N10");

            documents = snapshot.Content.GetById(4).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N11", "N12");

            documents = snapshot.Content.GetById(10).Children(_variationAccesor).ToArray();
            AssertDocuments(documents);
        }

        [Test]
        public void ParentTest()
        {
            Init(GetInvariantKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            Assert.IsNull(snapshot.Content.GetById(1).Parent);
            Assert.IsNull(snapshot.Content.GetById(2).Parent);
            Assert.IsNull(snapshot.Content.GetById(3).Parent);

            Assert.AreEqual(1, snapshot.Content.GetById(4).Parent?.Id);
            Assert.AreEqual(1, snapshot.Content.GetById(5).Parent?.Id);
            Assert.AreEqual(1, snapshot.Content.GetById(6).Parent?.Id);

            Assert.AreEqual(2, snapshot.Content.GetById(7).Parent?.Id);
            Assert.AreEqual(2, snapshot.Content.GetById(8).Parent?.Id);
            Assert.AreEqual(2, snapshot.Content.GetById(9).Parent?.Id);

            Assert.AreEqual(3, snapshot.Content.GetById(10).Parent?.Id);

            Assert.AreEqual(4, snapshot.Content.GetById(11).Parent?.Id);
            Assert.AreEqual(4, snapshot.Content.GetById(12).Parent?.Id);
        }

        [Test]
        public void MoveToRootTest()
        {
            Init(GetInvariantKits);

            // get snapshot
            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            // do some changes
            var kit = _source.Kits[10];
            _source.Kits[10] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), 1, "-1,10", 4, -1, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            // notify
            _snapshotService.Notify(new[] { new ContentCacheRefresher.JsonPayload(10, Guid.Empty, TreeChangeTypes.RefreshBranch) }, out _, out _);

            // changes that *I* make are immediately visible on the current snapshot
            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1", "N2", "N3", "N10");

            documents = snapshot.Content.GetById(3).Children(_variationAccesor).ToArray();
            AssertDocuments(documents);

            Assert.IsNull(snapshot.Content.GetById(10).Parent);
        }

        [Test]
        public void MoveFromRootTest()
        {
            Init(GetInvariantKits);

            // get snapshot
            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            // do some changes
            var kit = _source.Kits[1];
            _source.Kits[1] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), 1, "-1,3,10,1", 1, 10, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            // notify
            _snapshotService.Notify(new[] { new ContentCacheRefresher.JsonPayload(1, Guid.Empty, TreeChangeTypes.RefreshBranch) }, out _, out _);

            // changes that *I* make are immediately visible on the current snapshot
            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N2", "N3");

            documents = snapshot.Content.GetById(10).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N1");

            Assert.AreEqual(10, snapshot.Content.GetById(1).Parent?.Id);
        }

        [Test]
        public void ReOrderTest()
        {
            Init(GetInvariantKits);

            // get snapshot
            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            // do some changes
            var kit = _source.Kits[7];
            _source.Kits[7] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, kit.Node.Path, 1, kit.Node.ParentContentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            kit = _source.Kits[8];
            _source.Kits[8] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, kit.Node.Path, 3, kit.Node.ParentContentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            kit = _source.Kits[9];
            _source.Kits[9] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, kit.Node.Path, 2, kit.Node.ParentContentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            // notify
            _snapshotService.Notify(new[] { new ContentCacheRefresher.JsonPayload(kit.Node.ParentContentId, Guid.Empty, TreeChangeTypes.RefreshBranch) }, out _, out _);

            // changes that *I* make are immediately visible on the current snapshot
            var documents = snapshot.Content.GetById(kit.Node.ParentContentId).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N7", "N9", "N8");
        }

        [Test]
        public void MoveTest()
        {
            Init(GetInvariantKits);

            // get snapshot
            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            // do some changes
            var kit = _source.Kits[4];
            _source.Kits[4] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, kit.Node.Path, 2, kit.Node.ParentContentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            kit = _source.Kits[5];
            _source.Kits[5] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, kit.Node.Path, 3, kit.Node.ParentContentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            kit = _source.Kits[6];
            _source.Kits[6] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, kit.Node.Path, 4, kit.Node.ParentContentId, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            kit = _source.Kits[7];
            _source.Kits[7] = new ContentNodeKit
            {
                ContentTypeId = 2,
                Node = new ContentNode(kit.Node.Id, Guid.NewGuid(), kit.Node.Level, "-1,1,7", 1, 1, DateTime.Now, 0),
                DraftData = null,
                PublishedData = new ContentData
                {
                    Name = kit.PublishedData.Name,
                    Published = true,
                    TemplateId = 0,
                    VersionId = 1,
                    VersionDate = DateTime.Now,
                    WriterId = 0,
                    Properties = new Dictionary<string, PropertyData[]>(),
                    CultureInfos = new Dictionary<string, CultureVariation>()
                }
            };

            // notify
            _snapshotService.Notify(new[]
            {
                // removal must come first
                new ContentCacheRefresher.JsonPayload(2, Guid.Empty, TreeChangeTypes.RefreshBranch),
                new ContentCacheRefresher.JsonPayload(1, Guid.Empty, TreeChangeTypes.RefreshBranch)
            }, out _, out _);

            // changes that *I* make are immediately visible on the current snapshot
            var documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N7", "N4", "N5", "N6");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9", "N8");

            Assert.AreEqual(1, snapshot.Content.GetById(7).Parent?.Id);
        }

        [Test]
        public void Clear_Branch_Locked()
        {
            // This test replicates an issue we saw here https://github.com/umbraco/Umbraco-CMS/pull/7907#issuecomment-610259393
            // The data was sent to me and this replicates it's structure

            var paths = new Dictionary<int, string> { { -1, "-1" } };

            Init(() => new List<ContentNodeKit>
            {
                CreateInvariantKit(1, -1, 1, paths),    // first level
                CreateInvariantKit(2, 1, 1, paths),     // second level
                CreateInvariantKit(3, 2, 1, paths),     // third level

                CreateInvariantKit(4, 3, 1, paths),     // fourth level (we'll copy this one to the same level)

                CreateInvariantKit(5, 4, 1, paths),     // 6th level

                CreateInvariantKit(6, 5, 2, paths),     // 7th level
                CreateInvariantKit(7, 5, 3, paths),
                CreateInvariantKit(8, 5, 4, paths),
                CreateInvariantKit(9, 5, 5, paths),
                CreateInvariantKit(10, 5, 6, paths)
            });

            // get snapshot
            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();
            //This will set a flag to force creating a new Gen next time the store is locked (i.e. In Notify)
            contentStore.CreateSnapshot();

            // notify - which ensures there are 2 generations in the cache meaning each LinkedNode has a Next value.
            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(4, Guid.Empty, TreeChangeTypes.RefreshBranch)
            }, out _, out _);

            // refresh the branch again, this used to show the issue where a null ref exception would occur
            // because in the ClearBranchLocked logic, when SetValueLocked was called within a recursive call
            // to a child, we null out the .Value of the LinkedNode within the while loop because we didn't capture
            // this value before recursing.
            Assert.DoesNotThrow(() =>
                _snapshotService.Notify(new[]
                {
                    new ContentCacheRefresher.JsonPayload(4, Guid.Empty, TreeChangeTypes.RefreshBranch)
                }, out _, out _));
        }

        [Test]
        public void NestedVariationChildrenTest()
        {
            Init(GetNestedVariantKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            //TEST with en-us variation context

            _variationAccesor.VariationContext = new VariationContext("en-US");

            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1-en-US");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N7-en-US");

            //Get the invariant and list children, there's a variation context so it should return invariant AND en-us variants
            documents = snapshot.Content.GetById(4).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N10-en-US", "N11");

            //Get the variant and list children, there's a variation context so it should return invariant AND en-us variants
            documents = snapshot.Content.GetById(7).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N12-en-US", "N13");

            //TEST with fr-fr variation context

            _variationAccesor.VariationContext = new VariationContext("fr-FR");

            documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1-fr-FR");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N7-fr-FR");

            //Get the invariant and list children, there's a variation context so it should return invariant AND en-us variants
            documents = snapshot.Content.GetById(4).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N10-fr-FR", "N11");

            //Get the variant and list children, there's a variation context so it should return invariant AND en-us variants
            documents = snapshot.Content.GetById(7).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N12-fr-FR", "N13");

            //TEST specific cultures

            documents = snapshot.Content.GetAtRoot("fr-FR").ToArray();
            AssertDocuments(documents, "N1-fr-FR");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor, "fr-FR").ToArray();
            AssertDocuments(documents, "N4", "N7-fr-FR"); //NOTE: Returns invariant, this is expected
            documents = snapshot.Content.GetById(1).Children(_variationAccesor, "").ToArray();
            AssertDocuments(documents, "N4"); //Only returns invariant since that is what was requested

            documents = snapshot.Content.GetById(4).Children(_variationAccesor, "fr-FR").ToArray();
            AssertDocuments(documents, "N10-fr-FR", "N11"); //NOTE: Returns invariant, this is expected
            documents = snapshot.Content.GetById(4).Children(_variationAccesor, "").ToArray();
            AssertDocuments(documents, "N11"); //Only returns invariant since that is what was requested

            documents = snapshot.Content.GetById(7).Children(_variationAccesor, "fr-FR").ToArray();
            AssertDocuments(documents, "N12-fr-FR", "N13"); //NOTE: Returns invariant, this is expected
            documents = snapshot.Content.GetById(7).Children(_variationAccesor, "").ToArray();
            AssertDocuments(documents, "N13"); //Only returns invariant since that is what was requested

            //TEST without variation context
            // This will actually convert the culture to "" which will be invariant since that's all it will know how to do
            // This will return a NULL name for culture specific entities because there is no variation context

            _variationAccesor.VariationContext = null;

            documents = snapshot.Content.GetAtRoot().ToArray();
            //will return nothing because there's only variant at root
            Assert.AreEqual(0, documents.Length);
            //so we'll continue to getting the known variant, do not fully assert this because the Name will NULL
            documents = snapshot.Content.GetAtRoot("fr-FR").ToArray();
            Assert.AreEqual(1, documents.Length);

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4");

            //Get the invariant and list children
            documents = snapshot.Content.GetById(4).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N11");

            //Get the variant and list children
            documents = snapshot.Content.GetById(7).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N13");
        }

        [Test]
        public void VariantChildrenTest()
        {
            Init(GetVariantKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            _variationAccesor.VariationContext = new VariationContext("en-US");

            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1-en-US", "N2-en-US", "N3-en-US");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4-en-US", "N5-en-US", "N6-en-US");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9-en-US", "N8-en-US", "N7-en-US");

            documents = snapshot.Content.GetById(3).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N10-en-US");

            documents = snapshot.Content.GetById(4).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N11-en-US", "N12-en-US");

            documents = snapshot.Content.GetById(10).Children(_variationAccesor).ToArray();
            AssertDocuments(documents);


            _variationAccesor.VariationContext = new VariationContext("fr-FR");

            documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1-fr-FR", "N3-fr-FR");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4-fr-FR", "N6-fr-FR");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9-fr-FR", "N7-fr-FR");

            documents = snapshot.Content.GetById(3).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N10-fr-FR");

            documents = snapshot.Content.GetById(4).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N12-fr-FR");

            documents = snapshot.Content.GetById(10).Children(_variationAccesor).ToArray();
            AssertDocuments(documents);

            documents = snapshot.Content.GetById(1).Children(_variationAccesor, "*").ToArray();
            AssertDocuments(documents, "N4-fr-FR", null, "N6-fr-FR");
            AssertDocuments("en-US", documents, "N4-en-US", "N5-en-US", "N6-en-US");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor, "en-US").ToArray();
            AssertDocuments(documents, "N4-fr-FR", null, "N6-fr-FR");
            AssertDocuments("en-US", documents, "N4-en-US", "N5-en-US", "N6-en-US");

            documents = snapshot.Content.GetById(1).ChildrenForAllCultures.ToArray();
            AssertDocuments(documents, "N4-fr-FR", null, "N6-fr-FR");
            AssertDocuments("en-US", documents, "N4-en-US", "N5-en-US", "N6-en-US");


            documents = snapshot.Content.GetAtRoot("*").ToArray();
            AssertDocuments(documents, "N1-fr-FR", null, "N3-fr-FR");

            documents = snapshot.Content.GetById(1).DescendantsOrSelf(_variationAccesor).ToArray();
            AssertDocuments(documents, "N1-fr-FR", "N4-fr-FR", "N12-fr-FR", "N6-fr-FR");

            documents = snapshot.Content.GetById(1).DescendantsOrSelf(_variationAccesor, "*").ToArray();
            AssertDocuments(documents, "N1-fr-FR", "N4-fr-FR", null /*11*/, "N12-fr-FR", null /*5*/, "N6-fr-FR");
        }

        [Test]
        public void RemoveTest()
        {
            Init(GetInvariantKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1", "N2", "N3");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N5", "N6");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9", "N8", "N7");

            // notify
            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(3, Guid.Empty, TreeChangeTypes.Remove), // remove last
                new ContentCacheRefresher.JsonPayload(5, Guid.Empty, TreeChangeTypes.Remove), // remove middle
                new ContentCacheRefresher.JsonPayload(9, Guid.Empty, TreeChangeTypes.Remove), // remove first
            }, out _, out _);

            documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1", "N2");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N6");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N8", "N7");

            // notify
            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(1, Guid.Empty, TreeChangeTypes.Remove), // remove first
                new ContentCacheRefresher.JsonPayload(8, Guid.Empty, TreeChangeTypes.Remove), // remove
                new ContentCacheRefresher.JsonPayload(7, Guid.Empty, TreeChangeTypes.Remove), // remove
            }, out _, out _);

            documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N2");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents);
        }

        [Test]
        public void UpdateTest()
        {
            Init(GetInvariantKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();

            var parentNodes = contentStore.Test.GetValues(1);
            var parentNode = parentNodes[0];
            AssertLinkedNode(parentNode.contentNode, -1, -1, 2, 4, 6);
            Assert.AreEqual(1, parentNode.gen);

            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1", "N2", "N3");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N5", "N6");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9", "N8", "N7");

            // notify
            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(1, Guid.Empty, TreeChangeTypes.RefreshBranch),
                new ContentCacheRefresher.JsonPayload(2, Guid.Empty, TreeChangeTypes.RefreshNode),
            }, out _, out _);

            parentNodes = contentStore.Test.GetValues(1);
            Assert.AreEqual(2, parentNodes.Length);
            parentNode = parentNodes[1]; // get the first gen
            AssertLinkedNode(parentNode.contentNode, -1, -1, 2, 4, 6); // the structure should have remained the same
            Assert.AreEqual(1, parentNode.gen);
            parentNode = parentNodes[0]; // get the latest gen
            AssertLinkedNode(parentNode.contentNode, -1, -1, 2, 4, 6); // the structure should have remained the same
            Assert.AreEqual(2, parentNode.gen);

            documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1", "N2", "N3");

            documents = snapshot.Content.GetById(1).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N4", "N5", "N6");

            documents = snapshot.Content.GetById(2).Children(_variationAccesor).ToArray();
            AssertDocuments(documents, "N9", "N8", "N7");


        }

        [Test]
        public void AtRootTest()
        {
            Init(GetVariantWithDraftKits);

            var snapshot = _snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            _variationAccesor.VariationContext = new VariationContext("en-US");

            // N2 is draft only

            var documents = snapshot.Content.GetAtRoot().ToArray();
            AssertDocuments(documents, "N1-en-US", /*"N2-en-US",*/ "N3-en-US");

            documents = snapshot.Content.GetAtRoot(true).ToArray();
            AssertDocuments(documents, "N1-en-US", "N2-en-US", "N3-en-US");
        }

        [Test]
        public void Set_All_Fast_Sorted_Ensure_LastChildContentId()
        {
            //see https://github.com/umbraco/Umbraco-CMS/issues/6353

            IEnumerable<ContentNodeKit> GetKits()
            {
                var paths = new Dictionary<int, string> { { -1, "-1" } };

                yield return CreateInvariantKit(1, -1, 1, paths);
                yield return CreateInvariantKit(2, 1, 1, paths);
            }

            Init(GetKits);

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();

            var parentNodes = contentStore.Test.GetValues(1);
            var parentNode = parentNodes[0];
            AssertLinkedNode(parentNode.contentNode, -1, -1, -1, 2, 2);

            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(2, Guid.Empty, TreeChangeTypes.Remove)
            }, out _, out _);

            parentNodes = contentStore.Test.GetValues(1);
            parentNode = parentNodes[0];

            AssertLinkedNode(parentNode.contentNode, -1, -1, -1, -1, -1);
        }

        [Test]
        public void Remove_Node_Ensures_Linked_List()
        {
            // NOTE: these tests are not using real scopes, in which case a Scope does not control
            // how the snapshots generations work. We are forcing new snapshot generations manually.

            IEnumerable<ContentNodeKit> GetKits()
            {
                var paths = new Dictionary<int, string> { { -1, "-1" } };

                //root
                yield return CreateInvariantKit(1, -1, 1, paths);

                //children
                yield return CreateInvariantKit(2, 1, 1, paths);
                yield return CreateInvariantKit(3, 1, 2, paths); //middle child
                yield return CreateInvariantKit(4, 1, 3, paths);
            }

            Init(GetKits);

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();

            Assert.AreEqual(1, contentStore.Test.LiveGen);
            Assert.IsTrue(contentStore.Test.NextGen);

            var parentNode = contentStore.Test.GetValues(1)[0];
            Assert.AreEqual(1, parentNode.gen);
            AssertLinkedNode(parentNode.contentNode, -1, -1, -1, 2, 4);

            var child1 = contentStore.Test.GetValues(2)[0];
            Assert.AreEqual(1, child1.gen);
            AssertLinkedNode(child1.contentNode, 1, -1, 3, -1, -1);

            var child2 = contentStore.Test.GetValues(3)[0];
            Assert.AreEqual(1, child2.gen);
            AssertLinkedNode(child2.contentNode, 1, 2, 4, -1, -1);

            var child3 = contentStore.Test.GetValues(4)[0];
            Assert.AreEqual(1, child3.gen);
            AssertLinkedNode(child3.contentNode, 1, 3, -1, -1, -1);

            //This will set a flag to force creating a new Gen next time the store is locked (i.e. In Notify)
            contentStore.CreateSnapshot();

            Assert.IsFalse(contentStore.Test.NextGen);

            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(3, Guid.Empty, TreeChangeTypes.Remove) //remove middle child
            }, out _, out _);

            Assert.AreEqual(2, contentStore.Test.LiveGen);
            Assert.IsTrue(contentStore.Test.NextGen);

            var parentNodes = contentStore.Test.GetValues(1);
            Assert.AreEqual(1, parentNodes.Length); // the parent doesn't get changed, not new gen's are added
            parentNode = parentNodes[0];
            Assert.AreEqual(1, parentNode.gen); // the parent node's gen has not changed
            AssertLinkedNode(parentNode.contentNode, -1, -1, -1, 2, 4);

            child1 = contentStore.Test.GetValues(2)[0];
            Assert.AreEqual(2, child1.gen); // there is now 2x gen's of this item
            AssertLinkedNode(child1.contentNode, 1, -1, 4, -1, -1);

            child2 = contentStore.Test.GetValues(3)[0];
            Assert.AreEqual(2, child2.gen); // there is now 2x gen's of this item
            Assert.IsNull(child2.contentNode);  // because it doesn't exist anymore

            child3 = contentStore.Test.GetValues(4)[0];
            Assert.AreEqual(2, child3.gen); // there is now 2x gen's of this item
            AssertLinkedNode(child3.contentNode, 1, 2, -1, -1, -1);
        }

        [Test]
        public void Refresh_Node_Ensures_Linked_list()
        {
            // NOTE: these tests are not using real scopes, in which case a Scope does not control
            // how the snapshots generations work. We are forcing new snapshot generations manually.

            IEnumerable<ContentNodeKit> GetKits()
            {
                var paths = new Dictionary<int, string> { { -1, "-1" } };

                //root
                yield return CreateInvariantKit(100, -1, 1, paths);

                //site
                yield return CreateInvariantKit(2, 100, 1, paths);
                yield return CreateInvariantKit(1, 100, 2, paths); //middle child
                yield return CreateInvariantKit(3, 100, 3, paths);

                //children of 1
                yield return CreateInvariantKit(20, 1, 1, paths);
                yield return CreateInvariantKit(30, 1, 2, paths);
                yield return CreateInvariantKit(40, 1, 3, paths);
            }

            Init(GetKits);

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();

            Assert.AreEqual(1, contentStore.Test.LiveGen);
            Assert.IsTrue(contentStore.Test.NextGen);

            var middleNode = contentStore.Test.GetValues(1)[0];
            Assert.AreEqual(1, middleNode.gen);
            AssertLinkedNode(middleNode.contentNode, 100, 2, 3, 20, 40);

            //This will set a flag to force creating a new Gen next time the store is locked (i.e. In Notify)
            contentStore.CreateSnapshot();

            Assert.IsFalse(contentStore.Test.NextGen);

            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(1, Guid.Empty, TreeChangeTypes.RefreshNode)
            }, out _, out _);

            Assert.AreEqual(2, contentStore.Test.LiveGen);
            Assert.IsTrue(contentStore.Test.NextGen);

            middleNode = contentStore.Test.GetValues(1)[0];
            Assert.AreEqual(2, middleNode.gen);
            AssertLinkedNode(middleNode.contentNode, 100, 2, 3, 20, 40);
        }

        /// <summary>
        /// This addresses issue: https://github.com/umbraco/Umbraco-CMS/issues/6698
        /// </summary>
        /// <remarks>
        /// This test mimics if someone were to:
        ///  1) Unpublish a "middle child"
        ///  2) Save and publish it
        ///  3) Publish it with descendants
        ///  4) Repeat steps 2 and 3
        ///
        /// Which has caused an exception. To replicate this test:
        ///  1) RefreshBranch with kits for a branch where the top most node is unpublished
        ///  2) RefreshBranch with kits for the branch where the top most node is published
        ///  3) RefreshBranch with kits for the branch where the top most node is published
        ///  4) RefreshNode
        ///  5) RefreshBranch with kits for the branch where the top most node is published
        /// </remarks>
        [Test]
        public void Refresh_Branch_With_Alternating_Publish_Flags()
        {
            // NOTE: these tests are not using real scopes, in which case a Scope does not control
            // how the snapshots generations work. We are forcing new snapshot generations manually.

            IEnumerable<ContentNodeKit> GetKits()
            {
                var paths = new Dictionary<int, string> { { -1, "-1" } };

                //root
                yield return CreateInvariantKit(100, -1, 1, paths);

                //site
                yield return CreateInvariantKit(2, 100, 1, paths);
                yield return CreateInvariantKit(1, 100, 2, paths); //middle child
                yield return CreateInvariantKit(3, 100, 3, paths);

                //children of 1
                yield return CreateInvariantKit(20, 1, 1, paths);
                yield return CreateInvariantKit(30, 1, 2, paths);
                yield return CreateInvariantKit(40, 1, 3, paths);
            }

            //init with all published
            Init(GetKits);

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();

            var rootKit = _source.Kits[1].Clone(PublishedModelFactory);

            void ChangePublishFlagOfRoot(bool published, int assertGen, TreeChangeTypes changeType)
            {
                //This will set a flag to force creating a new Gen next time the store is locked (i.e. In Notify)
                contentStore.CreateSnapshot();

                Assert.IsFalse(contentStore.Test.NextGen);

                //Change the root publish flag
                var kit = rootKit.Clone(PublishedModelFactory);
                kit.DraftData = published ? null : kit.PublishedData;
                kit.PublishedData = published ? kit.PublishedData : null;
                _source.Kits[1] = kit;

                _snapshotService.Notify(new[]
                {
                    new ContentCacheRefresher.JsonPayload(1, Guid.Empty, changeType)
                }, out _, out _);

                Assert.AreEqual(assertGen, contentStore.Test.LiveGen);
                Assert.IsTrue(contentStore.Test.NextGen);

                //get the latest gen for content Id 1
                var (gen, contentNode) = contentStore.Test.GetValues(1)[0];
                Assert.AreEqual(assertGen, gen);
                //even when unpublishing/re-publishing/etc... the linked list is always maintained
                AssertLinkedNode(contentNode, 100, 2, 3, 20, 40);
            }

            //unpublish the root
            ChangePublishFlagOfRoot(false, 2, TreeChangeTypes.RefreshBranch);

            //publish the root (since it's not published, it will cause a RefreshBranch)
            ChangePublishFlagOfRoot(true, 3, TreeChangeTypes.RefreshBranch);

            //publish root + descendants
            ChangePublishFlagOfRoot(true, 4, TreeChangeTypes.RefreshBranch);

            //save/publish the root (since it's already published, it will just cause a RefreshNode
            ChangePublishFlagOfRoot(true, 5, TreeChangeTypes.RefreshNode);

            //publish root + descendants
            ChangePublishFlagOfRoot(true, 6, TreeChangeTypes.RefreshBranch);
        }

        [Test]
        public void Refresh_Branch_Ensures_Linked_List()
        {
            // NOTE: these tests are not using real scopes, in which case a Scope does not control
            // how the snapshots generations work. We are forcing new snapshot generations manually.

            IEnumerable<ContentNodeKit> GetKits()
            {
                var paths = new Dictionary<int, string> { { -1, "-1" } };

                //root
                yield return CreateInvariantKit(1, -1, 1, paths);

                //children
                yield return CreateInvariantKit(2, 1, 1, paths);
                yield return CreateInvariantKit(3, 1, 2, paths); //middle child
                yield return CreateInvariantKit(4, 1, 3, paths);
            }

            Init(GetKits);

            var snapshotService = (PublishedSnapshotService)_snapshotService;
            var contentStore = snapshotService.GetContentStore();

            Assert.AreEqual(1, contentStore.Test.LiveGen);
            Assert.IsTrue(contentStore.Test.NextGen);

            var parentNode = contentStore.Test.GetValues(1)[0];
            Assert.AreEqual(1, parentNode.gen);
            AssertLinkedNode(parentNode.contentNode, -1, -1, -1, 2, 4);

            var child1 = contentStore.Test.GetValues(2)[0];
            Assert.AreEqual(1, child1.gen);
            AssertLinkedNode(child1.contentNode, 1, -1, 3, -1, -1);

            var child2 = contentStore.Test.GetValues(3)[0];
            Assert.AreEqual(1, child2.gen);
            AssertLinkedNode(child2.contentNode, 1, 2, 4, -1, -1);

            var child3 = contentStore.Test.GetValues(4)[0];
            Assert.AreEqual(1, child3.gen);
            AssertLinkedNode(child3.contentNode, 1, 3, -1, -1, -1);

            //This will set a flag to force creating a new Gen next time the store is locked (i.e. In Notify)
            contentStore.CreateSnapshot();

            Assert.IsFalse(contentStore.Test.NextGen);

            _snapshotService.Notify(new[]
            {
                new ContentCacheRefresher.JsonPayload(3, Guid.Empty, TreeChangeTypes.RefreshBranch) //remove middle child
            }, out _, out _);

            Assert.AreEqual(2, contentStore.Test.LiveGen);
            Assert.IsTrue(contentStore.Test.NextGen);

            var parentNodes = contentStore.Test.GetValues(1);
            Assert.AreEqual(1, parentNodes.Length); // the parent doesn't get changed, not new gen's are added
            parentNode = parentNodes[0];
            Assert.AreEqual(1, parentNode.gen); // the parent node's gen has not changed
            AssertLinkedNode(parentNode.contentNode, -1, -1, -1, 2, 4);

            child1 = contentStore.Test.GetValues(2)[0];
            Assert.AreEqual(2, child1.gen); // there is now 2x gen's of this item
            AssertLinkedNode(child1.contentNode, 1, -1, 3, -1, -1);

            child2 = contentStore.Test.GetValues(3)[0];
            Assert.AreEqual(2, child2.gen); // there is now 2x gen's of this item
            AssertLinkedNode(child2.contentNode, 1, 2, 4, -1, -1);

            child3 = contentStore.Test.GetValues(4)[0];
            Assert.AreEqual(2, child3.gen); // there is now 2x gen's of this item
            AssertLinkedNode(child3.contentNode, 1, 3, -1, -1, -1);
        }

        [Test]
        public void MultipleCacheIteration()
        {
            //see https://github.com/umbraco/Umbraco-CMS/issues/7798
            Init(GetInvariantKits);
            var snapshot = this._snapshotService.CreatePublishedSnapshot(previewToken: null);
            _snapshotAccessor.PublishedSnapshot = snapshot;

            var items = snapshot.Content.GetByXPath("/root/itype");
            Assert.AreEqual(items.Count(), items.Count());
        }
        private void AssertLinkedNode(ContentNode node, int parent, int prevSibling, int nextSibling, int firstChild, int lastChild)
        {
            Assert.AreEqual(parent, node.ParentContentId);
            Assert.AreEqual(prevSibling, node.PreviousSiblingContentId);
            Assert.AreEqual(nextSibling, node.NextSiblingContentId);
            Assert.AreEqual(firstChild, node.FirstChildContentId);
            Assert.AreEqual(lastChild, node.LastChildContentId);
        }

        private void AssertDocuments(IPublishedContent[] documents, params string[] names)
        {
            Assert.AreEqual(names.Length, documents.Length);
            for (var i = 0; i < names.Length; i++)
                Assert.AreEqual(names[i], documents[i].Name);
        }

        private void AssertDocuments(string culture, IPublishedContent[] documents, params string[] names)
        {
            Assert.AreEqual(names.Length, documents.Length);
            for (var i = 0; i < names.Length; i++)
                Assert.AreEqual(names[i], documents[i].Name(_variationAccesor, culture));
        }
    }
}
