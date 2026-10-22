---
title: Your own APIs
description: Tag responses from your site's own controllers, record changes to their data, and purge by hand.
seo:
  image: /og/your-apis.png
---

A site's own API, anything outside the Delivery API whose responses a frontend caches, can join in. The repository's sample does this for a dictionary endpoint that serves Umbraco's dictionary items.

## Tag the responses

Put `[CacheTagged]` on a controller or action, and name what each response covers with the scoped `ICacheTagsResponse`:

```csharp
[ApiController]
[Route("api/dictionary")]
[CacheTagged(QueryParameters = ["locale"])]
public sealed class DictionaryController(ILanguageService languages, ICacheTagsResponse cacheTags) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string locale)
    {
        ILanguage? language = await languages.GetAsync(locale);
        if (language is null)
        {
            return NotFound();
        }

        cacheTags.Add($"x-dictionary-{language.IsoCode.ToLowerInvariant()}");
        // ... read the dictionary items in that language
        return Ok(items);
    }
}
```

The response gets a `response-` key and `umbraco`, like a Delivery API response, and the package records the keys it covers. Delivery API items in the result are found on their own.

- **Custom keys** start with `x-` and use lowercase letters, digits and hyphens, at most 200 characters. An item's `content-` or `media-` key works too.
- **`QueryParameters`** lists the parameters that select what a response covers, and only those are part of its key. None by default. Listing a parameter that takes any value makes a key per value.
- **`Recorded = false`** makes each response carry its keys in the header instead of being recorded, for an action whose input varies without end, such as a search.

## Record changes

When the data behind those responses changes, record a change with `ICacheTagsChanges`:

```csharp
public sealed class DictionaryCacheTags(ICacheTagsChanges changes, ILanguageService languages)
    : INotificationAsyncHandler<DictionaryItemSavedNotification>,
      INotificationHandler<DictionaryCacheRefresherNotification>
{
    // Saving a dictionary item changes every language's response.
    public async Task HandleAsync(DictionaryItemSavedNotification notification, CancellationToken cancellationToken)
        => changes.Record(
            (await languages.GetAllAsync()).Select(language => $"x-dictionary-{language.IsoCode.ToLowerInvariant()}"),
            waitFor: "dictionary",
            reason: "dictionary items saved");

    // Raised on every server, once its dictionary cache has the change.
    public void Handle(DictionaryCacheRefresherNotification notification) => changes.Applied("dictionary");
}
```

Register it in a composer:

```csharp
builder.AddNotificationAsyncHandler<DictionaryItemSavedNotification, DictionaryCacheTags>();
builder.AddNotificationHandler<DictionaryCacheRefresherNotification, DictionaryCacheTags>();
```

- **`waitFor`** names a signal. The purge waits until every server has reported it with `Applied(signal)`, which your site calls wherever that server refreshes its copy of the data, usually in a cache refresher's notification handler. Signals are lowercase letters, digits, dots and hyphens.
- **Without `waitFor`**, the purge goes at once. Use that for data every server reads straight from the database.
- **`cultures`** limits the purge to responses in those cultures, and to responses without one.
- **`reason`** is shown with the change in the backoffice.

## Purge by hand

Administrators can purge keys from the backoffice (**Settings > Advanced > Cache tags > Purge**), or through the Management API:

| Request | Does |
| --- | --- |
| `POST /umbraco/management/api/v1/cache-tags/purge` | Purges the keys in the JSON array body at once. `["umbraco"]` purges everything. |
| `GET .../cache-tags/purges?state=Dead` | Lists purges in a state: `Pending`, `Sent`, `Dead` or `Discarded`. |
| `POST .../cache-tags/purges/{id}/retry` | Sends a failed or set-aside purge again. |
| `POST .../cache-tags/purges/{id}/discard` | Sets a pending or failed purge aside. |
