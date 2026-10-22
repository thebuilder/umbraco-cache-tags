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
    }
}
