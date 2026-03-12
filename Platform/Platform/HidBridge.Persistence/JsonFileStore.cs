using System.Text.Json;
using System.Text.Json.Serialization;

namespace HidBridge.Persistence;

/// <summary>
/// Provides a thread-safe JSON file helper used by the file-backed persistence stores.
/// </summary>
/// <typeparam name="TModel">The serialized model type.</typeparam>
internal sealed class JsonFileStore<TModel>
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _path;
    private readonly Func<TModel> _createDefault;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes the JSON file store.
    /// </summary>
    /// <param name="path">The file path managed by this store.</param>
    /// <param name="createDefault">Creates the default model when the file does not exist yet.</param>
    public JsonFileStore(string path, Func<TModel> createDefault)
    {
        _path = path;
        _createDefault = createDefault;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// Reads the current model, applies a mutation, persists the result, and returns the updated model.
    /// </summary>
    /// <param name="mutate">The mutation to apply.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated model instance.</returns>
    public async Task<TModel> UpdateAsync(Func<TModel, TModel> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadCoreAsync(cancellationToken);
            var updated = mutate(current);
            await WriteCoreAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the current model from disk.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The current model instance or the configured default value.</returns>
    public async Task<TModel> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TModel> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return _createDefault();
        }

        await using var stream = File.OpenRead(_path);
        var model = await JsonSerializer.DeserializeAsync<TModel>(stream, SerializerOptions, cancellationToken);
        return model ?? _createDefault();
    }

    private async Task WriteCoreAsync(TModel model, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, model, SerializerOptions, cancellationToken);
        }

        File.Move(tempPath, _path, overwrite: true);
    }
}
