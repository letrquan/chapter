using System.Security.Cryptography;
using System.Text;

namespace Chapter.Core.Ai;

/// <summary>Where the credential the app is about to use came from.</summary>
public enum ApiKeySource
{
    /// <summary>Nothing is configured; generation is unavailable.</summary>
    None,

    /// <summary>A key typed into Chapter and encrypted under the user's Windows account.</summary>
    Stored,

    /// <summary><c>ANTHROPIC_API_KEY</c>, inherited from whatever launched the app.</summary>
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

    /// <summary>How to describe the credential in one phrase.</summary>
    public string Description => Source switch
    {
        ApiKeySource.Stored => $"a key saved in Chapter ({Hint})",
        ApiKeySource.Environment => $"ANTHROPIC_API_KEY ({Hint})",
        ApiKeySource.Profile => "your ant auth login profile",
        _ => "no credential",
    };
}

/// <summary>
/// Holds the Claude API key.
///
/// Deliberately not <c>settings.json</c>. That file is plaintext in <c>%LOCALAPPDATA%</c>,
/// it is documented as hand-editable, and the app already tells users to open it to
/// configure editors and commit policies — so anything written there is one screen-share
/// away from being read aloud. The key lives in its own file, encrypted with DPAPI under
/// the current user, which ties it to the Windows account rather than to the disk: copying
/// the file to another machine or another user yields nothing.
///
/// Three sources, in this order:
///
/// <list type="number">
/// <item>a key typed into Chapter — the most explicit statement of intent about *this* app,
/// so it wins;</item>
/// <item><c>ANTHROPIC_API_KEY</c>, which most people who already use the API have set;</item>
/// <item>an <c>ant auth login</c> profile, which the SDK resolves for us.</item>
/// </list>
///
/// The order only ever matters when two are present at once, and the UI names the one it
/// used — an inherited environment variable belonging to a different account is exactly the
/// kind of thing that should be visible rather than inferred.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>The environment variable the official SDK reads, kept identical on purpose.</summary>
    public const string EnvironmentVariable = "ANTHROPIC_API_KEY";

    /// <summary>
    /// Bound into the ciphertext as additional entropy, so a blob lifted out of this file
    /// cannot be decrypted by another program simply because it runs as the same user.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Chapter.ApiKey.v1");

    private readonly string _filePath;
    private readonly Func<string, string?> _environment;

    /// <param name="filePath">
    /// Where the encrypted key lives. Overridable so tests can point at a temp directory
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

    /// <summary>
    /// The key to authenticate with, or null when the caller should fall back to a profile.
    ///
    /// Returns the secret itself, so it goes straight into a client and nowhere else — never
    /// into the operation log, a bridge payload or an exception message.
    /// </summary>
    public string? ReadKey()
    {
        var stored = ReadStored();
        if (stored is not null) return stored;

        var environment = _environment(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(environment) ? null : environment.Trim();
    }

    /// <summary>
    /// Which credential is configured, without handing back the secret. This is what the UI
    /// asks, and it is deliberately a different method from <see cref="ReadKey"/> so a
    /// display path cannot accidentally acquire a key.
    /// </summary>
    /// <param name="profileAvailable">
    /// Whether the SDK resolved an <c>ant auth login</c> profile. Passed in rather than
    /// probed here: resolving one touches the network, and this method is called on every
    /// repaint of the commit box.
    /// </param>
    public ApiKeyState Read(bool profileAvailable = false)
    {
        var stored = ReadStored();
        if (stored is not null) return new ApiKeyState(ApiKeySource.Stored, Hint(stored));

        var environment = _environment(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
            return new ApiKeyState(ApiKeySource.Environment, Hint(environment.Trim()));

        return profileAvailable
            ? new ApiKeyState(ApiKeySource.Profile, null)
            : ApiKeyState.Missing;
    }

    /// <summary>
    /// Encrypts a key and writes it. An empty or whitespace value clears the stored key
    /// instead, which is how the UI offers "forget this key" without a second method.
    /// </summary>
    /// <returns>Null on success, or one sentence about why it could not be saved.</returns>
    public string? Store(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) return Clear();

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (directory is not null) Directory.CreateDirectory(directory);

            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(trimmed), Entropy, DataProtectionScope.CurrentUser);

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

    /// <summary>Removes the stored key. Anything in the environment is not ours to clear.</summary>
    public string? Clear()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The key could not be removed: {ex.Message}";
        }
    }

    private string? ReadStored()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;

            var plain = ProtectedData.Unprotect(
                File.ReadAllBytes(_filePath), Entropy, DataProtectionScope.CurrentUser);

            var key = Encoding.UTF8.GetString(plain).Trim();
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A blob written by another Windows account, or a corrupt file. Treat it as
            // absent rather than throwing: the next source down may well work, and a
            // credential problem must never be what stops the commit box rendering.
            return null;
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
