using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace TheBuilder.CacheTags.Example.Seeding;

/// <summary>
///     Creates data types, content types, media and documents for the seeders. The <c>Ensure</c> methods
///     skip an item whose key already exists, so a seeder can run against a database seeded by an older
///     version of it and add only what is missing.
/// </summary>
internal sealed class SeedWriter(
    IDataTypeService dataTypeService,
    IContentTypeService contentTypeService,
    IContentService contentService,
    IMediaService mediaService,
    MediaFileManager mediaFileManager,
    PropertyEditorCollection propertyEditors,
    IConfigurationEditorJsonSerializer configurationSerializer)
{
    public static readonly Guid User = Constants.Security.SuperUserKey;

    public async Task<IDataType> BuiltInDataType(Guid key) =>
        await dataTypeService.GetAsync(key) ?? throw new InvalidOperationException($"The built-in data type {key} is missing.");

    public async Task<IDataType> ExistingDataType(string name) =>
        await dataTypeService.GetAsync(name) ?? throw new InvalidOperationException($"The {name} data type is missing.");

    /// <summary>
    ///     Creates a data type, or returns it when one has the key, or (for the first seeder's data types,
    ///     which had no fixed key) the name.
    /// </summary>
    public async Task<IDataType> EnsureDataType(
        Guid? key,
        string name,
        string editorAlias,
        string editorUiAlias,
        ValueStorageType storage,
        IDictionary<string, object>? configuration = null)
    {
        IDataType? existing = (key is { } k ? await dataTypeService.GetAsync(k) : null) ?? await dataTypeService.GetAsync(name);
        if (existing is not null)
        {
            return existing;
        }

        IDataEditor editor = propertyEditors[editorAlias] ?? throw new InvalidOperationException($"No property editor {editorAlias}.");
        var dataType = new DataType(editor, configurationSerializer)
        {
            Name = name,
            EditorUiAlias = editorUiAlias,
            DatabaseType = storage,
            ConfigurationData = configuration ?? new Dictionary<string, object>(),
        };
        if (key is { } newKey)
        {
            dataType.Key = newKey;
        }

        Attempt<IDataType, DataTypeOperationStatus> attempt = await dataTypeService.CreateAsync(dataType, User);
        return attempt.Success ? attempt.Result : throw new InvalidOperationException($"Creating the {name} data type failed: {attempt.Status}");
    }

    /// <summary>Creates the content type <paramref name="build" /> describes, unless its key exists.</summary>
    public async Task EnsureContentType(Guid key, Func<ContentType> build)
    {
        if (contentTypeService.Get(key) is not null)
        {
            return;
        }

        ContentType contentType = build();
        contentType.Key = key;
        Attempt<ContentTypeOperationStatus> attempt = await contentTypeService.CreateAsync(contentType, User);
        if (attempt.Success is false)
        {
            throw new InvalidOperationException($"Creating the {contentType.Alias} content type failed: {attempt.Result}");
        }
    }

    /// <summary>Lets <paramref name="parentKey" /> have <paramref name="childKeys" /> as children, adding any it lacks.</summary>
    public async Task EnsureAllowedChildren(Guid parentKey, params Guid[] childKeys)
    {
        IContentType parent = contentTypeService.Get(parentKey) ?? throw new InvalidOperationException($"No content type {parentKey}.");
        List<ContentTypeSort> allowed = parent.AllowedContentTypes?.ToList() ?? [];
        var missing = childKeys.Where(child => allowed.All(sort => sort.Key != child)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (Guid child in missing)
        {
            IContentType childType = contentTypeService.Get(child) ?? throw new InvalidOperationException($"No content type {child}.");
            allowed.Add(new ContentTypeSort(child, allowed.Count, childType.Alias));
        }

        parent.AllowedContentTypes = allowed;
        Attempt<ContentTypeOperationStatus> attempt = await contentTypeService.UpdateAsync(parent, User);
        if (attempt.Success is false)
        {
            throw new InvalidOperationException($"Allowing children under {parent.Alias} failed: {attempt.Result}");
        }
    }

    public IMedia EnsureFolder(Guid key, string name, IMedia? parent = null) =>
        mediaService.GetById(key) ?? Save(CreateMedia(key, name, parent, Constants.Conventions.MediaTypes.Folder));

    /// <summary>A gradient PNG, so the frontend has a real file to show without binaries in the repository.</summary>
    public IMedia EnsureImage(Guid key, string name, Rgba32 from, Rgba32 to, IMedia? parent = null)
    {
        if (mediaService.GetById(key) is { } existing)
        {
            return existing;
        }

        IMedia media = CreateMedia(key, name, parent, Constants.Conventions.MediaTypes.Image);

        const int width = 1200;
        const int height = 675;
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                Span<Rgba32> row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var t = (x + y) / (float)(width + height);
                    row[x] = new Rgba32(
                        (byte)(from.R + ((to.R - from.R) * t)),
                        (byte)(from.G + ((to.G - from.G) * t)),
                        (byte)(from.B + ((to.B - from.B) * t)));
                }
            }
        });

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        var url = StoreFile(media, $"{name.ToLowerInvariant()}.png", stream);

        media.SetValue(Constants.Conventions.Media.File, JsonSerializer.Serialize(new { src = url, crops = Array.Empty<object>() }));
        media.SetValue(Constants.Conventions.Media.Width, width);
        media.SetValue(Constants.Conventions.Media.Height, height);
        media.SetValue(Constants.Conventions.Media.Bytes, stream.Length);
        media.SetValue(Constants.Conventions.Media.Extension, "png");
        return Save(media);
    }

    /// <summary>A one-page PDF with <paramref name="lines" /> of text, built by hand.</summary>
    public IMedia EnsurePdf(Guid key, string name, string fileName, IMedia? parent, params string[] lines)
    {
        if (mediaService.GetById(key) is { } existing)
        {
            return existing;
        }

        IMedia media = CreateMedia(key, name, parent, Constants.Conventions.MediaTypes.File);
        using var stream = new MemoryStream(Pdf(lines));
        var url = StoreFile(media, fileName, stream);

        // The File type's umbracoFile is an upload field: the path alone, not the Image type's JSON.
        media.SetValue(Constants.Conventions.Media.File, url);
        media.SetValue(Constants.Conventions.Media.Bytes, stream.Length);
        media.SetValue(Constants.Conventions.Media.Extension, "pdf");
        return Save(media);
    }

    public IContent? Get(Guid key) => contentService.GetById(key);

    /// <summary>Creates and publishes an invariant document, unless its key exists.</summary>
    public IContent EnsurePage(Guid key, string name, IContent? parent, string contentTypeAlias, Action<IContent> setValues) =>
        EnsureDocument(key, name, parent, contentTypeAlias, setValues, publish: ["*"]);

    /// <summary>
    ///     Creates a document unless its key exists, and publishes the cultures in <paramref name="publish" />
    ///     (<c>*</c> for an invariant document). An empty list saves it unpublished.
    /// </summary>
    public IContent EnsureDocument(Guid key, string name, IContent? parent, string contentTypeAlias, Action<IContent> setValues, string[] publish)
    {
        if (contentService.GetById(key) is { } existing)
        {
            return existing;
        }

        IContent content = contentService.Create(name, parent?.Id ?? Constants.System.Root, contentTypeAlias);
        content.Key = key;
        setValues(content);
        contentService.Save(content);
        if (publish.Length > 0)
        {
            Publish(content, name, publish);
        }

        return content;
    }

    public void SaveAndPublish(IContent content, string name)
    {
        contentService.Save(content);
        Publish(content, name, ["*"]);
    }

    private void Publish(IContent content, string name, string[] cultures)
    {
        PublishResult result = contentService.Publish(content, cultures);
        if (result.Success is false)
        {
            throw new InvalidOperationException($"Publishing {name} failed: {result.Result}");
        }
    }

    private IMedia CreateMedia(Guid key, string name, IMedia? parent, string mediaTypeAlias)
    {
        IMedia media = mediaService.CreateMedia(name, parent?.Id ?? Constants.System.Root, mediaTypeAlias);
        media.Key = key;
        return media;
    }

    private string StoreFile(IMedia media, string fileName, Stream stream)
    {
        stream.Position = 0;
        IPropertyType fileProperty = media.Properties[Constants.Conventions.Media.File]?.PropertyType
            ?? throw new InvalidOperationException($"The {media.ContentType.Alias} media type has no umbracoFile property.");
        var path = mediaFileManager.StoreFile(media, fileProperty, fileName, stream, null);
        return mediaFileManager.FileSystem.GetUrl(path);
    }

    private IMedia Save(IMedia media)
    {
        Attempt<OperationResult?> result = mediaService.Save(media);
        return result.Success ? media : throw new InvalidOperationException($"Saving the {media.Name} media failed.");
    }

    private static byte[] Pdf(string[] lines)
    {
        var text = string.Join(" T* ", lines.Select(line => $"({line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj"));
        var page = $"BT /F1 18 Tf 72 760 Td 26 TL {text} ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(page)} >>\nstream\n{page}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
