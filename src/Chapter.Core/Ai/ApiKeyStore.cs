using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chapter.Core.Ai;

/// <summary>Where the credential the app is about to use came from.</summary>
public enum ApiKeySource
{
    /// <summary>Nothing is configured; generation is unavailable.</summary>
    None,

    /// <summary>A key typed into Chapter and encrypted under the user's Windows account.</summary>
    Stored,

    /// <summary>The provider's environment variable, inherited from whatever launched the app.</summary>
    Environment,

    /// <summary>An <c>ant auth login</c> profile under the user's config directory.</summary>
    Profile,
}

/// <summary>
/// The credential, and enough about it to tell the user which one is in play.
///
/// <see cref="Hint"/> is the last few characters and never the key: it exists so somebody
/// with two accounts can tell which is configured without the app ever putting a usable
/// secret on screen, in a log line, or in an error message.
/// </summary>
public sealed record ApiKeyState(ApiKeySource Source, string? Hint)
{
    public bool HasKey => Source is not ApiKeySource.None;

    public static readonly ApiKeyState Missing = new(ApiKeySource.None, null);
}

/// <summary>
/// Holds the API keys.
///
/// Deliberately not <c>settings.json</c>. That file is plaintext in <c>%LOCALAPPDATA%</c>,
/// it is documented as hand-editable, and the app already tells users to open it to
/// configure editors and commit policies — so anything written there is one screen-share
/// away from being read aloud. Keys live in their own file, encrypted with DPAPI under the
/// current user, which ties them to the Windows account rather than to the disk: copying the
/// file to another machine or another user yields nothing.
///
/// One file, keyed by provider, because a Claude key and an OpenAI key are two different
/// secrets and somebody switching between them should not have to retype either. For each
/// provider, three sources in this order:
///
/// <list type="number">
/// <item>a key typed into Chapter — the most explicit statement of intent about *this* app,
/// so it wins;</item>
/// <item>that provider's environment variable, which most people who already use the API
/// have set;</item>
/// <item>for Anthropic only, an <c>ant auth login</c> profile, which the SDK resolves.</item>
/// </list>
///
/// The order only ever matters when two are present at once, and the UI names the one it
/// used — an inherited environment variable belonging to a different account is exactly the
/// kind of thing that should be visible rather than inferred.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>The variable the Anthropic SDK reads, kept identical on purpose.</summary>
    public const string AnthropicVariable = "ANTHROPIC_API_KEY";

    /// <summary>The variable every OpenAI-compatible client reads, for the same reason.</summary>
    public const string OpenAiVariable = "OPENAI_API_KEY";

    /// <summary>
    /// Bound into the ciphertext as additional entropy, so a blob lifted out of this file
    /// cannot be decrypted by another program simply because it runs as the same user.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Chapter.ApiKey.v1");

    private readonly string _filePath;
    private readonly Func<string, string?> _environment;

    /// <param name="filePath">
    /// Where the encrypted keys live. Overridable so tests can point at a temp directory
    /// rather than writing into the user's profile.
    /// </param>
    /// <param name="environment">
    /// How to read an environment variable. Overridable for the same reason: the fallback
    /// order is worth testing, and a test that reads the real environment passes or fails
    /// depending on whether whoever ran it happens to have a key exported.
    /// </param>
    public ApiKeyStore(string? filePath = null, Func<string, string?>? environment = null)
    {
        _filePath = filePath ?? DefaultFilePath;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public static string DefaultFilePath =>
        Path.Combine(AppSettings.DirectoryPath, "credentials.dat");

    /// <summary>Which environment variable a provider reads.</summary>
    public static string EnvironmentVariableFor(string provider) =>
        provider == "openai" ? OpenAiVariable : AnthropicVariable;

    /// <summary>
    /// The key to authenticate with, or null when there is none.
    ///
    /// Returns the secret itself, so it goes straight into a client and nowhere else — never
    /// into the operation log, a bridge payload or an exception message.
    /// </summary>
    public string? ReadKey(string provider)
    {
        if (ReadAll().TryGetValue(provider, out var stored) && stored.Length > 0) return stored;

        var environment = _environment(EnvironmentVariableFor(provider));
        return string.IsNullOrWhiteSpace(environment) ? null : environment.Trim();
    }

    /// <summary>
    /// Which credential is configured, without handing back the secret. This is what the UI
    /// asks, and it is deliberately a different method from <see cref="ReadKey"/> so a
    /// display path cannot accidentally acquire a key.
    /// </summary>
    public ApiKeyState Read(string provider)
    {
        if (ReadAll().TryGetValue(provider, out var stored) && stored.Length > 0)
            return new ApiKeyState(ApiKeySource.Stored, Hint(stored));

        var environment = _environment(EnvironmentVariableFor(provider));

        return string.IsNullOrWhiteSpace(environment)
            ? ApiKeyState.Missing
            : new ApiKeyState(ApiKeySource.Environment, Hint(environment.Trim()));
    }

    /// <summary>
    /// Encrypts a key and writes it. An empty or whitespace value forgets that provider's
    /// key instead, which is how the UI offers "forget this key" without a second method.
    /// </summary>
    /// <returns>Null on success, or one sentence about why it could not be saved.</returns>
    public string? Store(string provider, string key)
    {
        var keys = new Dictionary<string, string>(ReadAll(), StringComparer.Ordinal);
        var trimmed = key.Trim();

        if (trimmed.Length == 0) keys.Remove(provider);
        else keys[provider] = trimmed;

        return Write(keys);
    }

    /// <summary>Forgets one provider's key. Anything in the environment is not ours to clear.</summary>
    public string? Clear(string provider) => Store(provider, "");

    private string? Write(Dictionary<string, string> keys)
    {
        try
        {
            if (keys.Count == 0)
            {
                if (File.Exists(_filePath)) File.Delete(_filePath);
                return null;
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(keys)), Entropy,
                DataProtectionScope.CurrentUser);

            // Written through a temp file in the same directory and moved into place, for the
            // same reason WorkingTreeWriter does it: a half-written credentials file is not a
            // recoverable state, it is a key the app can no longer decrypt.
            var temporary = _filePath + ".tmp";
            File.WriteAllBytes(temporary, cipher);
            File.Move(temporary, _filePath, overwrite: true);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return $"The key could not be saved: {ex.Message}";
        }
    }

    /// <summary>
    /// Every stored key, decrypted.
    ///
    /// A file written before this app knew about more than one provider holds a bare key
    /// rather than a JSON object, so anything that does not parse is read as the Anthropic
    /// one — which is what it was. Losing somebody's key to a format change would be a poor
    /// way to introduce a second provider.
    /// </summary>
    private Dictionary<string, string> ReadAll()
    {
        try
        {
            if (!File.Exists(_filePath)) return [];

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_filePath), Entropy, DataProtectionScope.CurrentUser);

            var text = Encoding.UTF8.GetString(plain).Trim();
            if (text.Length == 0) return [];

            if (text[0] != '{')
                return new Dictionary<string, string>(StringComparer.Ordinal) { ["anthropic"] = text };

            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) is { } keys
                ? new Dictionary<string, string>(keys, StringComparer.Ordinal)
                : [];
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // A blob written by another Windows account, or a corrupt file. Treat it as
            // absent rather than throwing: the environment may well answer instead, and a
            // credential problem must never be what stops the commit box rendering.
            return [];
        }
    }

    /// <summary>
    /// The tail of a key, for telling two accounts apart.
    ///
    /// Four characters, and only when there are enough to spare — a short string is more
    /// likely to be a typo than a key, and echoing most of it back would defeat the point.
    /// </summary>
    internal static string Hint(string key) =>
        key.Length >= 12 ? "…" + key[^4..] : "…";
}
