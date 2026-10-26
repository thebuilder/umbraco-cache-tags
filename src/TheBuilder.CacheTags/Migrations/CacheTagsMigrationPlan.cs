using Umbraco.Cms.Core.Packaging;

namespace TheBuilder.CacheTags.Migrations;

internal sealed class CacheTagsMigrationPlan : PackageMigrationPlan
{
    public CacheTagsMigrationPlan() : base(Constants.PackageName) { }

    protected override void DefinePlan()
    {
        To<InstallCacheTagsSchema>(new Guid("6f1d2b0e-4c8a-4a57-9e2b-3d7c5a1f8e40"));
        To<AddDeliverySchema>(new Guid("b3e8f2a1-0d4c-4f7e-8a6b-1c9d5e2f7a30"));
        To<AddWriteLock>(new Guid("4d7a9c2e-6b1f-4e38-9a05-8f3c2d1e6b74"));
        To<AddIndexAcks>(new Guid("9b2e6f14-3a7d-4c51-8e09-d6f1a2b7c3e5"));
        To<AddTargets>(new Guid("c41f7a2e-8d3b-4e96-a5c0-2b7e9d4f1a68"));
        To<AddReleasedIndex>(new Guid("5e0b8d37-2f6a-4c19-b4e2-7a9d1c3f6e05"));
        To<DropMappingTables>(new Guid("1a2213c1-eacb-4959-a77b-b28bc0891544"));
    }
}
