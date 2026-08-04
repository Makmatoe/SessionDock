using System.ComponentModel;
using System.IO;
using System.Net.Http;
using SessionDock.ReleaseTrust;
using Velopack.Exceptions;

namespace SessionDock.Services;

internal sealed record UpdateFailurePresentation(
    string TitleKey,
    string DetailKey,
    string BadgeKey,
    StatusTone Tone)
{
    public static UpdateFailurePresentation Create(Exception exception)
    {
        if (TryCreate(exception, out var presentation))
            return presentation;

        throw new ArgumentException(
            "The exception is not an expected update failure.",
            nameof(exception));
    }

    public static bool TryCreate(
        Exception exception,
        out UpdateFailurePresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(exception);

        presentation = exception switch
        {
            ReleaseTrustException => new(
                "UpdateFailure.Trust.Title",
                "UpdateFailure.Trust.Detail",
                "UpdateFailure.Badge.Rejected",
                StatusTone.Error),
            AcquireLockFailedException => new(
                "UpdateFailure.Busy.Title",
                "UpdateFailure.Busy.Detail",
                "UpdateFailure.Badge.Busy",
                StatusTone.Warning),
            ChecksumFailedException => new(
                "UpdateFailure.DownloadRejected.Title",
                "UpdateFailure.Checksum.Detail",
                "UpdateFailure.Badge.Rejected",
                StatusTone.Error),
            InvalidDataException => new(
                "UpdateFailure.DownloadRejected.Title",
                "UpdateFailure.InvalidData.Detail",
                "UpdateFailure.Badge.Rejected",
                StatusTone.Error),
            NotInstalledException => new(
                "UpdateFailure.PortableRefreshRequired.Title",
                "UpdateFailure.PortableRefreshRequired.Detail",
                "UpdateFailure.Badge.ManualUpdate",
                StatusTone.Error),
            TaskCanceledException => new(
                "UpdateFailure.Timeout.Title",
                "UpdateFailure.Timeout.Detail",
                "UpdateFailure.Badge.NetworkTimeout",
                StatusTone.Error),
            OperationCanceledException => new(
                "UpdateFailure.Timeout.Title",
                "UpdateFailure.Cancelled.Detail",
                "UpdateFailure.Badge.NetworkTimeout",
                StatusTone.Error),
            TimeoutException => new(
                "UpdateFailure.Timeout.Title",
                "UpdateFailure.Timeout.Detail",
                "UpdateFailure.Badge.NetworkTimeout",
                StatusTone.Error),
            HttpIOException => new(
                "UpdateFailure.Interrupted.Title",
                "UpdateFailure.Interrupted.Detail",
                "UpdateFailure.Badge.NetworkError",
                StatusTone.Error),
            HttpRequestException => new(
                "UpdateFailure.Unreachable.Title",
                "UpdateFailure.Unreachable.Detail",
                "UpdateFailure.Badge.NetworkError",
                StatusTone.Error),
            UnauthorizedAccessException => new(
                "UpdateFailure.AccessDenied.Title",
                "UpdateFailure.AccessDenied.Detail",
                "UpdateFailure.Badge.AccessDenied",
                StatusTone.Error),
            IOException => new(
                "UpdateFailure.FilesUnavailable.Title",
                "UpdateFailure.FilesUnavailable.Detail",
                "UpdateFailure.Badge.FileError",
                StatusTone.Error),
            Win32Exception => new(
                "UpdateFailure.UpdaterStart.Title",
                "UpdateFailure.UpdaterStart.Detail",
                "UpdateFailure.Badge.UpdaterError",
                StatusTone.Error),
            _ => null!
        };
        return presentation is not null;
    }
}
