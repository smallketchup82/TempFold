// Copyright (c) 2026 smallketchup82 <ketchup@smkt.ca>. Licensed under the MIT License.
// See the LICENSE file in the repository root for full license text.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Polly;
using Polly.Retry;
using TempFold.Control;
using TempFold.Models;

namespace TempFold.Integrations;

internal sealed class FahService(ControllerOptions options, ILogger<FahService> logger) : BackgroundService
{
    private readonly Lock stateLock = new();
    private readonly Channel<bool> intentChanges = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

    // Shared with sensor callers and the status display. Network awaits stay outside this lock.
    private bool? desiredFolding;
    private bool startsInhibited;
    private bool pausePending;
    private bool? manualFolding;
    private long controlRevision;
    private CancellationTokenSource controlCancellation = new();
    private FoldingStatus? status;

    public bool IsConnected => GetStatus() is not null;
    public event Action<bool>? ConnectionChanged;
    public event Action? StatusChanged;
    private int shutdownStarted;

    public FoldingStatus? GetStatus()
    {
        lock (stateLock)
            return status;
    }

    private void publishStatus(FoldingStatus? value)
    {
        bool changed;
        lock (stateLock)
        {
            changed = status != value;
            status = value;
        }
        if (changed)
        {
            logger.LogInformation("{logType}: connected {fahConnected}, folding {folding}, working {working}",
                "fahStateChanged", value is not null, value?.IsConfiguredToFold, value?.IsWorking);
            StatusChanged?.Invoke();
        }
    }

    public void SetFolding(bool folding)
    {
        lock (stateLock)
        {
            // While paused, temperature decisions cannot reassert either state
            // against a user operating the FAH web UI.
            if (startsInhibited || desiredFolding == folding)
                return;

            logger.LogDebug("{logType}: requested folding {desiredFolding}, next revision {commandRevision}",
                "fahIntentChanged", folding, controlRevision + 1);
            desiredFolding = folding;
            cancelPreviousControl();
        }

        // Coalesce repeated decisions: the connection loop reads the latest intent.
        intentChanges.Writer.TryWrite(true);
    }

    public void SetPaused(bool paused)
    {
        lock (stateLock)
        {
            if (!paused && !startsInhibited) return;
            startsInhibited = paused;
            desiredFolding = null;
            pausePending = paused;
            manualFolding = null;
            cancelPreviousControl();
        }
        intentChanges.Writer.TryWrite(true);
    }

    public void SetFoldingManually(bool folding)
    {
        lock (stateLock)
        {
            if (!startsInhibited) return;
            manualFolding = folding;
            pausePending = false;
            cancelPreviousControl();
        }
        intentChanges.Writer.TryWrite(true);
    }

    private void cancelPreviousControl()
    {
        // A newer decision cancels an in-flight send as well as future retries.
        controlRevision++;
        controlCancellation.Cancel();
        controlCancellation.Dispose();
        controlCancellation = new();
    }

    private void completeOneTimeRequest(long revision)
    {
        lock (stateLock)
            if (controlRevision == revision && startsInhibited)
            {
                pausePending = false;
                manualFolding = null;
            }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ResiliencePipeline reconnectPolicy = createReconnectPolicy();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await reconnectPolicy.ExecuteAsync(runSessionAttemptAsync, stoppingToken);

                // A stable session has ended. Begin a fresh backoff sequence after one second.
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown also cancels Polly's retry delay.
        }
    }

    private ResiliencePipeline createReconnectPolicy() =>
        new ResiliencePipelineBuilder()
            .AddRetry(
                new RetryStrategyOptions
                {
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromMinutes(15),
                    MaxRetryAttempts = int.MaxValue,
                    ShouldHandle = arguments => new ValueTask<bool>(
                        !arguments.Context.CancellationToken.IsCancellationRequested
                        && isConnectionFailure(arguments.Outcome.Exception)),
                    OnRetry = arguments =>
                    {
                        logger.LogWarning(
                            "Folding@home unavailable: {Reason}. Retrying in {Seconds}s",
                            arguments.Outcome.Exception!.Message,
                            arguments.RetryDelay.TotalSeconds);

                        return default;
                    }
                })
            .Build();

    private async ValueTask runSessionAttemptAsync(CancellationToken cancellationToken)
    {
        var sessionLifetime = Stopwatch.StartNew();

        try
        {
            await runSessionAsync(cancellationToken);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested && isConnectionFailure(exception))
        {
            // Keep increasing Polly's backoff if the client repeatedly drops new connections.
            if (sessionLifetime.Elapsed < TimeSpan.FromSeconds(30))
                throw;

            // Returning ends this retry execution, so the next session starts with fresh backoff.
            logger.LogWarning(
                "Folding@home disconnected: {Reason}. Retrying in 1s",
                exception.Message);
        }
        finally
        {
            bool wasConnected;
            lock (stateLock) wasConnected = status is not null;
            publishStatus(null);
            if (wasConnected) ConnectionChanged?.Invoke(false);
        }
    }

    private async Task runSessionAsync(CancellationToken stoppingToken)
    {
        using var connection = new FahConnection();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        CancellationToken sessionToken = sessionCancellation.Token;

        // Mutable client state belongs to this session. Publish only its computed status.
        var clientState = new FahState();
        JsonNode? initialState = await connection.ConnectAsync(options.FahEndpoint, sessionToken);
        clientState.Apply(initialState, initial: true);

        publishStatus(clientState.Status);
        ConnectionChanged?.Invoke(true);

        logger.LogInformation("Connected to Folding@home at {Endpoint}", options.FahEndpoint);

        Task<JsonNode?> nextMessage = connection.ReceiveAsync(sessionToken);
        Task<bool> nextIntentChange = intentChanges.Reader.ReadAsync(sessionToken).AsTask();

        bool? unconfirmedFolding = null;
        long commandRevision = 0;
        long commandSentAt = 0;

        try
        {
            while (true)
            {
                bool? requestedFolding;
                bool oneTimeRequest;
                bool forceCommand;
                long revision;
                CancellationToken controlToken;

                lock (stateLock)
                {
                    forceCommand = manualFolding.HasValue;
                    oneTimeRequest = pausePending || forceCommand;
                    requestedFolding = manualFolding ?? (pausePending ? false : desiredFolding);
                    revision = controlRevision;
                    controlToken = controlCancellation.Token;
                }

                if (requestedFolding is null)
                    unconfirmedFolding = null;
                else if (oneTimeRequest && !forceCommand && unconfirmedFolding == false)
                {
                    // A stop was already sent. Entering an inhibitor relinquishes
                    // ongoing stop enforcement so external controls remain usable.
                    completeOneTimeRequest(revision);
                    unconfirmedFolding = null;
                    requestedFolding = null;
                }

                if (requestedFolding is bool folding)
                {
                    bool replacesUnconfirmedCommand = unconfirmedFolding is not null && unconfirmedFolding != folding;
                    bool needsCommand = unconfirmedFolding is null && !clientState.Matches(folding);

                    // An opposite command must be sent even if the cached state already matches it.
                    if (forceCommand || replacesUnconfirmedCommand || needsCommand)
                    {
                        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, controlToken);
                        await connection.SetFoldingAsync(folding, commandCancellation.Token);

                        if (oneTimeRequest)
                        {
                            completeOneTimeRequest(revision);
                            unconfirmedFolding = null;
                        }
                        else
                        {
                            unconfirmedFolding = folding;
                            commandRevision = revision;
                            commandSentAt = Stopwatch.GetTimestamp();
                        }

                        logger.LogInformation("{logType}: requested {action}, revision {commandRevision}, one time {oneTime}",
                            "fahCommandSent", folding ? "fold" : "pause", revision, oneTimeRequest);
                    }
                    else if (oneTimeRequest && unconfirmedFolding is null)
                    {
                        // FAH already reports paused. An inhibitor blocks our starts
                        // but leaves subsequent external controls alone.
                        completeOneTimeRequest(revision);
                    }
                }

                // Wake for a client update, a new decision, or an expired confirmation deadline.
                await waitForActivityAsync(
                    nextMessage,
                    nextIntentChange,
                    unconfirmedFolding is not null,
                    commandSentAt,
                    sessionToken);

                if (nextMessage.IsCompleted)
                {
                    JsonNode? message = await nextMessage;
                    clientState.Apply(message);

                    publishStatus(clientState.Status);

                    bool receivedStateUpdate = message is JsonArray;
                    if (receivedStateUpdate && unconfirmedFolding is bool pending && clientState.Matches(pending))
                    {
                        logger.LogInformation("{logType}: confirmed folding {folding}, revision {commandRevision}",
                            "fahCommandConfirmed", pending, commandRevision);
                        unconfirmedFolding = null;
                        if (!pending) completeOneTimeRequest(commandRevision);
                    }

                    nextMessage = connection.ReceiveAsync(sessionToken);
                }

                if (nextIntentChange.IsCompleted)
                {
                    await nextIntentChange;
                    nextIntentChange = intentChanges.Reader.ReadAsync(sessionToken).AsTask();
                }
            }
        }
        finally
        {
            sessionCancellation.Cancel();

            // Finish both outstanding waits before the next session uses the intent channel.
            try
            {
                await Task.WhenAll(nextMessage, nextIntentChange);
            }
            catch (Exception) when (sessionCancellation.IsCancellationRequested)
            {
                // The session's original failure is already propagating to the retry policy.
            }
        }
    }

    private static async Task waitForActivityAsync(
        Task nextMessage,
        Task nextIntentChange,
        bool awaitingConfirmation,
        long commandSentAt,
        CancellationToken sessionToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);

        if (awaitingConfirmation)
        {
            TimeSpan remaining = TimeSpan.FromSeconds(30) - Stopwatch.GetElapsedTime(commandSentAt);
            if (remaining <= TimeSpan.Zero)
                throw confirmationTimeout();

            deadline.CancelAfter(remaining);
        }

        try
        {
            await Task.WhenAny(nextMessage, nextIntentChange).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!sessionToken.IsCancellationRequested)
        {
            throw confirmationTimeout();
        }
    }

    private static bool isConnectionFailure(Exception? exception) =>
        exception is WebSocketException
            or IOException
            or OperationCanceledException
            or JsonException
            or InvalidOperationException
            or ArgumentException
            or TimeoutException;

    private static TimeoutException confirmationTimeout() =>
        new("Folding@home did not confirm the requested state within 30s.");

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0) return;
        // Stop the intent loop before sending the final pause so it cannot resume FAH.
        await base.StopAsync(cancellationToken);
        if (!options.PauseFahOnExit) return;

        // Shutdown has canceled the active socket. Use a short, independent connection
        // to attempt the pause even when shutdown was caused by an outage.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var connection = new FahConnection();
            await connection.ConnectAsync(options.FahEndpoint, timeout.Token);
            await connection.SetFoldingAsync(false, timeout.Token);
            logger.LogInformation("{logType}: {action} on application exit", "fahCommandSent", "pause");
        }
        catch (Exception exception) when (isConnectionFailure(exception))
        {
            logger.LogWarning("Could not pause Folding@home on exit: {Reason}", exception.Message);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        lock (stateLock)
            controlCancellation.Dispose();
    }
}
