using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using OnlineMongoMigrationProcessor;
using OnlineMongoMigrationProcessor.Context;
using OnlineMongoMigrationProcessor.Models;
using System.Text.Json;

namespace MongoMigrationWebApp.Controller
{
    [ApiController]
    [Route("api/migration-jobs")]
    public class MigrationJobsController : ControllerBase
    {
        // Callers authenticate with the same password that gates the UI.
        public const string AppPasswordHeader = "X-Migration-App-Password";

        private readonly Service.JobManager _jobManager;
        private readonly Service.PasswordManager _passwordManager;

        public MigrationJobsController(Service.JobManager jobManager, Service.PasswordManager passwordManager)
        {
            _jobManager = jobManager;
            _passwordManager = passwordManager;
        }

        [HttpPost("reset")]
        public async Task<IActionResult> Reset()
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            if (_jobManager.GetMigrationIds().Any(_jobManager.IsProcessRunning))
                return Conflict("Cannot reset while a migration job is running.");

            return Ok(new { deletedJobs = _jobManager.ClearAllJobFiles() });
        }

        /// <summary>
        /// Deletes one job, so a seeded job can be re-imported without resetting every job on the
        /// instance. Mirrors the delete button on the jobs page, including its running-job guard.
        /// </summary>
        [HttpDelete("{jobId}")]
        public async Task<IActionResult> Delete(string jobId)
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            var job = _jobManager.GetMigrationJobById(jobId);
            if (job == null)
                return NotFound($"No job with id {jobId}.");

            if (_jobManager.IsProcessRunning(jobId))
                return Conflict($"Job {job.Name} is running. Pause it before deleting.");

            var name = job.Name;
            _jobManager.ClearJobFiles(jobId);

            // CurrentlyActiveJob reloads from this id, so leaving it set resurrects the job from
            // disk and every later import is rejected as a cross-job write.
            if (string.Equals(MigrationJobContext.ActiveMigrationJobId, jobId, StringComparison.Ordinal))
            {
                MigrationJobContext.ActiveMigrationJobId = string.Empty;
                MigrationJobContext.ClearCurrentlyActiveJobCache();
            }

            return Ok(new { jobId, name, deleted = true });
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            var runningJobId = _jobManager.GetRunningJobId();
            var jobs = new List<object>();

            foreach (var id in _jobManager.GetMigrationIds() ?? new List<string>())
            {
                var job = _jobManager.GetMigrationJobById(id);
                if (job == null)
                    continue;

                jobs.Add(Summarize(job, _jobManager.GetMigrationUnits(job), runningJobId));
            }

            return Ok(new { runningJobId, count = jobs.Count, jobs });
        }

        [HttpGet("{jobId}")]
        public async Task<IActionResult> Get(string jobId)
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest("jobId is required.");

            var job = _jobManager.GetMigrationJobById(jobId);
            if (job == null)
                return NotFound($"No migration job with id '{jobId}'.");

            var units = _jobManager.GetMigrationUnits(job) ?? new List<MigrationUnit>();

            return Ok(new
            {
                job = Summarize(job, units, _jobManager.GetRunningJobId()),
                units = units.Select(u => new
                {
                    u.Id,
                    u.DatabaseName,
                    u.CollectionName,
                    targetDatabaseName = u.GetEffectiveTargetDatabaseName(),
                    targetCollectionName = u.GetEffectiveTargetCollectionName(),
                    u.DumpPercent,
                    u.RestorePercent,
                    u.IndexPercent,
                    u.DumpComplete,
                    u.RestoreComplete,
                    u.EstimatedDocCount,
                    u.ActualDocCount,
                    u.AvgDocSizeBytes,
                    sourceStatus = u.SourceStatus.ToString(),
                    u.FailedOperation,
                    u.SkippedDueToMaxRetries,
                    opLogError = u.OpLogError.ToString(),
                    u.BulkCopyStartedOn,
                    u.BulkCopyEndedOn,
                    u.ChangeStreamStartedOn,
                    u.CSLastChecked,
                    u.CSLastChangeUTCTime
                })
            });
        }

        /// <summary>
        /// Shapes a job for the read APIs. MigrationJob holds endpoints rather than connection
        /// strings, so nothing here needs redacting.
        /// </summary>
        private object Summarize(MigrationJob job, List<MigrationUnit>? units, string? runningJobId)
        {
            units ??= new List<MigrationUnit>();

            return new
            {
                job.Id,
                job.Name,
                jobType = job.JobType.ToString(),
                cdcMode = job.CDCMode.ToString(),
                changeStreamLevel = job.ChangeStreamLevel.ToString(),
                changeStreamMode = job.ChangeStreamMode.ToString(),
                job.SourceEndpoint,
                job.TargetEndpoint,
                job.SourceServerVersion,
                job.IsStarted,
                job.IsCompleted,
                job.IsCancelled,
                job.IsSimulatedRun,
                job.SyncBackEnabled,
                isRunning = job.Id == runningJobId,
                job.StartedOn,
                job.CSLastChecked,
                units = new
                {
                    total = units.Count,
                    dumpComplete = units.Count(u => u.DumpComplete),
                    restoreComplete = units.Count(u => u.RestoreComplete),
                    failed = units.Count(u => !string.IsNullOrEmpty(u.FailedOperation)),
                    estimatedDocs = units.Sum(u => u.EstimatedDocCount),
                    actualDocs = units.Sum(u => u.ActualDocCount)
                }
            };
        }

        [HttpGet("{jobId}/logs")]
        public async Task<IActionResult> GetLogs(string jobId)
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest("jobId is required.");

            var logBucket = _jobManager.GetLogBucket(jobId, out string fileName, out bool isLiveLog);
            return Ok(new
            {
                jobId,
                isLiveLog,
                fileName,
                logs = logBucket.Logs ?? new List<LogObject>()
            });
        }

        [HttpPost("{jobId}/start")]
        public async Task<IActionResult> Start(string jobId, [FromBody] MigrationJobStartRequest? request)
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest("jobId is required.");

            var job = _jobManager.GetMigrationJobById(jobId);
            if (job == null)
                return NotFound($"No migration job with id '{jobId}'.");

            if (job.IsCompleted)
                return Conflict("Job is already completed.");

            var runningJobId = _jobManager.GetRunningJobId();
            if (!string.IsNullOrEmpty(runningJobId))
            {
                return runningJobId == jobId
                    ? Conflict("Job is already running.")
                    : Conflict($"Another migration job is running ({runningJobId}).");
            }

            // Body wins so a rotated credential can be supplied; otherwise reuse what the job was
            // started with, which is the whole point of not having to re-enter them after a recycle.
            string source, target;
            if (!string.IsNullOrWhiteSpace(request?.SourceConnectionString)
                && !string.IsNullOrWhiteSpace(request?.TargetConnectionString))
            {
                source = request!.SourceConnectionString!;
                target = request!.TargetConnectionString!;
                _jobManager.RememberConnectionStrings(job.Id, source, target);
            }
            else if (!_jobManager.TryResolveConnectionStrings(job.Id, out source, out target))
            {
                return BadRequest(
                    "No stored connection strings for this job. Supply sourceConnectionString and targetConnectionString.");
            }

            // Endpoints are what the job records, so a mismatch means these credentials point
            // somewhere else entirely -- refuse rather than migrate into the wrong cluster.
            var sourceEndpoint = Helper.ExtractHost(source);
            var targetEndpoint = Helper.ExtractHost(target);
            if (!string.IsNullOrEmpty(job.SourceEndpoint) && job.SourceEndpoint != sourceEndpoint)
                return BadRequest($"Source endpoint '{sourceEndpoint}' does not match the job's '{job.SourceEndpoint}'.");
            if (!string.IsNullOrEmpty(job.TargetEndpoint) && job.TargetEndpoint != targetEndpoint)
                return BadRequest($"Target endpoint '{targetEndpoint}' does not match the job's '{job.TargetEndpoint}'.");

            // Both flags are mutated by the calls below, so report the pre-start values.
            var resumed = job.IsStarted;
            var syncBack = job.ProcessingSyncBack && !job.IsSimulatedRun;

            // The Blazor pages set this themselves before starting and JobManager.StartMigration does
            // not, so the API path left it false. MongoDumpRestoreCordinator stops its timer while it
            // is false, which killed every DumpAndRestore job one tick after it started.
            // SyncBackProcessor sets it on its own path.
            if (!syncBack)
                job.IsStarted = true;

            MigrationJobContext.SaveMigrationJob(job);
            MigrationJobContext.SaveJobList();

            if (syncBack)
            {
                await _jobManager.SyncBackToSource(source, target, job);
            }
            else
            {
                await _jobManager.StartMigration(
                    job, source, target, job.NameSpaces ?? string.Empty, job.JobType, Helper.IsOnline(job));
            }

            return Accepted(new
            {
                jobId = job.Id,
                name = job.Name,
                started = true,
                resumed,
                syncBack,
                sourceEndpoint = job.SourceEndpoint,
                targetEndpoint = job.TargetEndpoint
            });
        }

        /// <summary>
        /// Cut Over: drain the forward change stream, then mark the job terminal. Irreversible.
        /// </summary>
        /// <remarks>
        /// Mirrors the UI's Cut Over path, including the part that is easy to get wrong: it asks
        /// the change stream to close gracefully and waits for the processor's outer loop to exit,
        /// rather than calling StopMigration, which cancels the token and drops the in-flight batch.
        /// </remarks>
        [HttpPost("{jobId}/cutover")]
        public async Task<IActionResult> Cutover(string jobId, [FromBody] MigrationJobCutoverRequest? request)
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            var job = _jobManager.GetMigrationJobById(jobId);
            if (job == null)
                return NotFound($"No job with id {jobId}.");

            if (job.IsCompleted)
                return Conflict($"Job {job.Name} has already completed.");

            if (!Helper.IsOnline(job))
                return Conflict($"Job {job.Name} is Offline. It completes on its own and has no cut over.");

            // Before IsOfflineJobCompleted, which dereferences this list without a null check.
            var units = job.MigrationUnitBasics ?? new List<MigrationUnitBasic>();
            if (units.Count == 0)
                return Conflict($"Job {job.Name} has no collections to cut over.");

            // The two conditions the UI uses to enable the button.
            if (!Helper.IsOfflineJobCompleted(job))
                return Conflict($"Job {job.Name} has not finished its bulk copy.");

            var indexPending = units
                .Where(mu => Helper.IsMigrationUnitValid(mu) && !mu.IndexBuildComplete && mu.IndexPercent < 100)
                .Select(mu => $"{mu.DatabaseName}.{mu.CollectionName}")
                .ToList();
            if (indexPending.Count > 0)
            {
                return Conflict($"Index builds are still running on {indexPending.Count} collection(s): "
                    + string.Join(", ", indexPending.Take(5)) + ".");
            }

            // The UI only warns about drain in a dialog a human reads. There is no human here, so
            // cutting over with changes still in flight has to be asked for explicitly.
            var pending = units.Where(Helper.IsMigrationUnitValid).Sum(mu => mu.CSUpdatesInLastBatch);
            if (pending > 0 && !(request?.Force ?? false))
            {
                return Conflict($"The forward change stream reported {pending} change(s) in its last batch. "
                    + "Wait for two consecutive batches at 0, or pass force=true to cut over anyway.");
            }

            job.PendingAction = PendingChangeStreamAction.Cutover;
            MigrationJobContext.SaveMigrationJob(job);
            MigrationJobContext.RequestChangeStreamAutoClose("API: Cut Over");

            try
            {
                await WaitForChangeStreamProcessorExitAsync();
            }
            finally
            {
                MigrationJobContext.ResetChangeStreamAutoClose();
            }

            job.IsCancelled = true;
            job.IsCompleted = true;
            job.PendingAction = PendingChangeStreamAction.None;
            MigrationJobContext.SaveMigrationJob(job);

            return Ok(new
            {
                jobId = job.Id,
                name = job.Name,
                cutOver = true,
                forced = request?.Force ?? false,
                pendingChangesAtCutover = pending
            });
        }

        /// <summary>
        /// Pauses a running job. "controlled" stops taking new work and lets in-flight chunks
        /// finish; "immediate" cancels everything now.
        /// </summary>
        [HttpPost("{jobId}/pause")]
        public async Task<IActionResult> Pause(string jobId, [FromBody] MigrationJobPauseRequest? request)
        {
            var denied = await AuthorizeAsync();
            if (denied != null)
                return denied;

            var job = _jobManager.GetMigrationJobById(jobId);
            if (job == null)
                return NotFound($"No job with id {jobId}.");

            var mode = (request?.Mode ?? "controlled").Trim().ToLowerInvariant();
            if (mode != "controlled" && mode != "immediate")
                return BadRequest("mode must be 'controlled' or 'immediate'.");

            if (mode == "controlled")
            {
                MigrationJobContext.RequestControlledPause("API: Controlled Pause");
            }
            else
            {
                // Off the request thread: StopMigration waits on the worker for up to 10 seconds.
                await Task.Run(() => _jobManager.StopMigration());
            }

            MigrationJobContext.SaveMigrationJob(job);
            return Ok(new { jobId = job.Id, name = job.Name, paused = true, mode });
        }

        /// <summary>
        /// Bounded the same way the UI bounds it: two change-stream batch durations plus a minute.
        /// </summary>
        private static async Task WaitForChangeStreamProcessorExitAsync()
        {
            var batchDurationSec = 120;
            try
            {
                var cfg = new MigrationSettings();
                cfg.Load();
                if (cfg.ChangeStreamBatchDuration > 0)
                    batchDurationSec = cfg.ChangeStreamBatchDuration;
            }
            catch
            {
                // Fall back to the default bound; a missing settings file must not block cut over.
            }

            var deadline = DateTime.UtcNow.AddSeconds((batchDurationSec * 2) + 60);
            while (DateTime.UtcNow < deadline && MigrationJobContext.IsChangeStreamProcessorRunning)
                await Task.Delay(500);
        }

        [HttpPost("import")]
        public async Task<IActionResult> Import([FromBody] MigrationJobImportRequest request)
        {
            try
            {
                var denied = await AuthorizeAsync();
                if (denied != null)
                    return denied;

                if (request?.Job == null
                    || string.IsNullOrWhiteSpace(request.SourceConnectionString)
                    || string.IsNullOrWhiteSpace(request.TargetConnectionString))
                {
                    return BadRequest("Job, sourceConnectionString, and targetConnectionString are required.");
                }

                var importedJob = JsonConvert.DeserializeObject<MigrationJob>(request.Job.Value.GetRawText());
                if (importedJob == null || string.IsNullOrWhiteSpace(importedJob.Name))
                    return BadRequest("Job.Name is required.");

                importedJob.SourceEndpoint = Helper.ExtractHost(request.SourceConnectionString);
                importedJob.TargetEndpoint = Helper.ExtractHost(request.TargetConnectionString);

                var existingJob = MigrationJobContext.JobList?.MigrationJobIds?
                    .Select(MigrationJobContext.GetMigrationJob)
                    .FirstOrDefault(job => string.Equals(job?.Name, importedJob.Name, StringComparison.Ordinal));

                importedJob.Id = existingJob?.Id ?? Guid.NewGuid().ToString();
                importedJob.IsStarted = false;
                importedJob.IsCompleted = false;
                importedJob.IsCancelled = false;

                // Through the manager so they also reach the ConnectionStringVault. Assigning the
                // in-memory dictionaries directly left an imported job with nothing on disk, so a
                // recycle before it was ever started lost the strings and the viewer could then
                // only offer a resume with updated ones.
                _jobManager.RememberConnectionStrings(importedJob.Id, request.SourceConnectionString, request.TargetConnectionString);

                if (request.Settings.HasValue)
                {
                    var appSettings = new MigrationSettings();
                    appSettings.Load();
                    // PopulateObject merges only the properties present in the payload, so callers
                    // can override one setting without restating the rest.
                    JsonConvert.PopulateObject(request.Settings.Value.GetRawText(), appSettings);
                    if (!appSettings.Save(out var appSettingsError))
                        return StatusCode(StatusCodes.Status500InternalServerError, appSettingsError);
                }

                if (request.SourceCaCertificatePem != null)
                {
                    var settings = new MigrationSettings();
                    settings.Load();
                    settings.CACertContentsForSourceServer = request.SourceCaCertificatePem;
                    if (!settings.Save(out var settingsError))
                        return StatusCode(StatusCodes.Status500InternalServerError, settingsError);
                }

                if (MigrationJobContext.JobList == null)
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, "Migration job storage is not initialized.");

                MigrationJobContext.JobList.MigrationJobIds ??= new List<string>();
                if (!MigrationJobContext.JobList.MigrationJobIds.Contains(importedJob.Id))
                    MigrationJobContext.JobList.MigrationJobIds.Add(importedJob.Id);

                importedJob.MigrationUnitBasics ??= new List<MigrationUnitBasic>();
                if (request.Collections != null && request.Collections.Count > 0)
                {
                    // SaveMigrationUnit rejects any unit whose JobId differs from the active job, so
                    // importing while a different job is active discards every unit and still returns 200.
                    var activeJob = MigrationJobContext.CurrentlyActiveJob;
                    if (activeJob != null
                        && !string.Equals(activeJob.Id, importedJob.Id, StringComparison.Ordinal)
                        && activeJob.IsStarted && !activeJob.IsCompleted && !activeJob.IsCancelled)
                    {
                        return Conflict($"Migration job '{activeJob.Name}' is still running. Stop it before importing.");
                    }

                    MigrationJobContext.ActiveMigrationJobId = importedJob.Id;

                    var collectionJson = JsonConvert.SerializeObject(request.Collections);
                    var units = await Helper.PopulateJobCollectionsAsync(importedJob, collectionJson, request.SourceConnectionString);

                    if (!Helper.AddMigrationUnits(units, importedJob, MigrationJobContext.Logger))
                    {
                        return StatusCode(StatusCodes.Status500InternalServerError,
                            $"Resolved {units.Count} collection(s) for '{importedJob.Name}' but persisted only "
                            + $"{importedJob.MigrationUnitBasics.Count}. See the job log for the failing namespace.");
                    }

                    if (importedJob.MigrationUnitBasics.Count == 0)
                    {
                        return StatusCode(StatusCodes.Status500InternalServerError,
                            $"Resolved {units.Count} collection(s) for '{importedJob.Name}' but persisted none.");
                    }
                }

                if (!MigrationJobContext.SaveMigrationJob(importedJob) || !MigrationJobContext.SaveJobList())
                    return StatusCode(StatusCodes.Status500InternalServerError, "Failed to persist imported job.");

                return Ok(new { importedJob.Id, importedJob.Name, importedJob.MigrationUnitBasics?.Count });
            }
            catch (Exception ex)
            {
                Helper.LogToFile($"Migration job import failed: {ex}", "ImportMigrationJobs.txt");
                return StatusCode(StatusCodes.Status500InternalServerError, ex.Message);
            }
        }

        /// <summary>
        /// Returns the response to send when the caller is not allowed, or null when it is.
        /// Behind an out-of-process reverse proxy (IIS/ANCM, kubectl port-forward) every request
        /// looks loopback, so the source address alone is not treated as an authentication factor.
        /// </summary>
        private async Task<IActionResult?> AuthorizeAsync()
        {
            if (!IsLoopbackRequest())
            {
                return StatusCode(StatusCodes.Status403Forbidden,
                    "This endpoint is only available from the machine hosting the app.");
            }

            if (!await _passwordManager.IsPasswordSetAsync())
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "Set the application password in the web UI before using the migration job API.");
            }

            if (!await _passwordManager.ValidatePasswordAsync(Request.Headers[AppPasswordHeader].ToString()))
                return StatusCode(StatusCodes.Status401Unauthorized, $"A valid {AppPasswordHeader} header is required.");

            return null;
        }

        private bool IsLoopbackRequest()
        {
            var address = HttpContext.Connection.RemoteIpAddress;
            return address != null && System.Net.IPAddress.IsLoopback(address);
        }
    }

    public sealed class MigrationJobImportRequest
    {
        public JsonElement? Job { get; set; }
        public List<CollectionInfo>? Collections { get; set; }
        public string? SourceConnectionString { get; set; }
        public string? TargetConnectionString { get; set; }
        public string? SourceCaCertificatePem { get; set; }
        public JsonElement? Settings { get; set; }
    }

    public sealed class MigrationJobStartRequest
    {
        // Both optional: omit them to reuse what the job was started with.
        public string? SourceConnectionString { get; set; }
        public string? TargetConnectionString { get; set; }
    }

    public sealed class MigrationJobCutoverRequest
    {
        /// <summary>
        /// Cut over even though the forward change stream still reports pending changes. The UI
        /// only warns about this in a dialog; over the API there is no human to read it, so it
        /// has to be asked for explicitly.
        /// </summary>
        public bool Force { get; set; }
    }

    public sealed class MigrationJobPauseRequest
    {
        /// <summary>
        /// "controlled" stops taking new chunks and lets in-flight ones finish; "immediate"
        /// cancels everything now. Defaults to controlled, which is the safe one.
        /// </summary>
        public string? Mode { get; set; }
    }
}
