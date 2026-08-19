using System.Text.RegularExpressions;

namespace VideoDownLoader.Services;

public enum YtDlpFailureKind
{
    Unknown,
    HttpForbidden,
    PoTokenRequired,
    JavaScriptRuntime,
    AuthenticationRequired,
    BotCheck,
    RateLimited,
    FormatUnavailable,
    VideoUnavailable,
    DrmProtected,
    Network,
    Disk
}

public sealed record YtDlpFailure(
    YtDlpFailureKind Kind,
    string UserMessage,
    bool CanRepairByUpdating,
    string DiagnosticTail);

public sealed class YtDlpException : InvalidOperationException
{
    public YtDlpException(int exitCode, YtDlpFailure failure)
        : base(failure.UserMessage)
    {
        ExitCode = exitCode;
        Failure = failure;
    }

    public int ExitCode { get; }
    public YtDlpFailure Failure { get; }
}

public static class YtDlpDiagnostics
{
    private static readonly Regex UrlSecretsRegex = new(
        @"(?i)(?<name>(?:sig|signature|lsig|pot|po_token|token|key|expire|ip)=)[^&\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static YtDlpFailure Classify(IEnumerable<string> lines)
    {
        var tail = string.Join(Environment.NewLine, lines.TakeLast(80));
        var sanitized = Sanitize(tail);

        if (Contains(tail, "DRM protected", "DRM-protected", "protected by DRM", "This video is DRM"))
        {
            return Failure(YtDlpFailureKind.DrmProtected,
                "Источник защищён DRM и не предоставляет обычный доступный формат.", false, sanitized);
        }

        if (Contains(tail, "Sign in to confirm you’re not a bot", "Sign in to confirm you're not a bot"))
        {
            return Failure(YtDlpFailureKind.BotCheck,
                "YouTube запросил проверку от бота. Выберите cookies браузера; если этого недостаточно, подключите PO Token provider.",
                true, sanitized);
        }

        if (Contains(tail, "missing_pot", "missing pot", "No PO Token provided", "PO Token is required", "requires a PO Token", "GVS PO Token missing"))
        {
            return Failure(YtDlpFailureKind.PoTokenRequired,
                "YouTube требует Proof-of-Origin token. Подключите совместимый PO Token provider в настройках YouTube.",
                true, sanitized);
        }

        if (Contains(tail, "No supported JavaScript runtime", "JS Challenge Providers: none", "Error running deno"))
        {
            return Failure(YtDlpFailureKind.JavaScriptRuntime,
                "Не удалось выполнить JavaScript-проверку YouTube. Обновите инструменты приложения.", true, sanitized);
        }

        if (Contains(tail, "HTTP Error 403", "403 Forbidden", "Server returned 403"))
        {
            return Failure(YtDlpFailureKind.HttpForbidden,
                "YouTube отклонил адрес медиапотока (HTTP 403). Приложение обновит YouTube-компоненты и повторит попытку один раз.",
                true, sanitized);
        }

        if (Contains(tail, "HTTP Error 429", "Too Many Requests"))
        {
            return Failure(YtDlpFailureKind.RateLimited,
                "Источник временно ограничил частоту запросов. Сделайте паузу и повторите загрузку позже.", false, sanitized);
        }

        if (Contains(tail, "login required", "Sign in to confirm your age", "members-only", "private video"))
        {
            return Failure(YtDlpFailureKind.AuthenticationRequired,
                "Для этого материала требуется авторизация. Выберите браузер с подходящей сессией YouTube.", false, sanitized);
        }

        if (Contains(tail, "Requested format is not available"))
        {
            return Failure(YtDlpFailureKind.FormatUnavailable,
                "Выбранный формат больше недоступен. Повторно проверьте ссылку и выберите формат заново.", true, sanitized);
        }

        if (Contains(tail, "Video unavailable", "This video is not available"))
        {
            return Failure(YtDlpFailureKind.VideoUnavailable,
                "YouTube сообщил, что видео недоступно для текущего региона или сеанса.", false, sanitized);
        }

        if (Contains(tail, "No space left on device", "There is not enough space"))
        {
            return Failure(YtDlpFailureKind.Disk,
                "На диске закончилось свободное место.", false, sanitized);
        }

        if (Contains(tail,
                "Unable to connect",
                "Connection reset",
                "ConnectionResetError",
                "Connection aborted",
                "Удаленный хост принудительно разорвал",
                "Удалённый хост принудительно разорвал",
                "timed out",
                "Temporary failure"))
        {
            return Failure(YtDlpFailureKind.Network,
                "Сетевая ошибка при обращении к источнику. Проверьте соединение и повторите попытку.", false, sanitized);
        }

        return Failure(YtDlpFailureKind.Unknown,
            "yt-dlp не смог завершить загрузку. Диагностический хвост сохранён в журнале.", false, sanitized);
    }

    public static string Sanitize(string value) => UrlSecretsRegex.Replace(value, "${name}<скрыто>");

    private static bool Contains(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static YtDlpFailure Failure(
        YtDlpFailureKind kind,
        string message,
        bool canRepair,
        string tail) => new(kind, message, canRepair, tail);
}
