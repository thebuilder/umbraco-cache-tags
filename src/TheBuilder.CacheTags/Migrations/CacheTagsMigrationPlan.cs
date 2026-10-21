using Umbraco.Cms.Core.Packaging;

namespace TheBuilder.CacheTags.Migrations;

internal sealed class CacheTagsMigrationPlan : PackageMigrationPlan
{
    public CacheTagsMigrationPlan() : base(Constants.PackageName) { }

    protected override void DefinePlan()
    {
        To<InstallCacheTagsSchema>(new Guid("6f1d2b0e-4c8a-4a57-9e2b-3d7c5a1f8e40"));
    }
}
