using MediaBrowser.Controller.Library;

namespace RussianMetadata;

// Align persisted grouping names during the scan completion phase. The creation
// guard also protects in-flight groups if native/plugin task ordering changes.
public sealed class CollectionNamePostScanTask(CollectionNameSynchronizer names) : ILibraryPostScanTask
{
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await names.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
