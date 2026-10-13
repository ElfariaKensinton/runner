using GitHub.DistributedTask.WebApi;
using GitHub.DistributedTask.Pipelines.ContextData;
using GitHub.Runner.Worker.Container;
using GitHub.Runner.Common;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GitHub.Runner.Worker
{
    [ServiceLocator(Default = typeof(FileCommandManager))]
    public interface IFileCommandManager : IRunnerService
    {
        void InitializeFiles(IExecutionContext context, ContainerInfo container);
        void ProcessFiles(IExecutionContext context, ContainerInfo container);

    }

    public sealed class FileCommandManager : RunnerService, IFileCommandManager
    {
        private const string _folderName = "_runner_file_commands";
        private List<IFileCommandExtension> _commandExtensions;
        private string _fileSuffix = String.Empty;
        private string _fileCommandDirectory;
        private Tracing _trace;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _trace = HostContext.GetTrace(nameof(FileCommandManager));

            _fileCommandDirectory = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Temp), _folderName);
            if (!Directory.Exists(_fileCommandDirectory))
            {
                Directory.CreateDirectory(_fileCommandDirectory);
            }

            var extensionManager = hostContext.GetService<IExtensionManager>();
            _commandExtensions = extensionManager.GetExtensions<IFileCommandExtension>() ?? new List<IFileCommandExtension>();
        }

        public void InitializeFiles(IExecutionContext context, ContainerInfo container)
        {
            var oldSuffix = _fileSuffix;
            _fileSuffix = Guid.NewGuid().ToString();
            foreach (var fileCommand in _commandExtensions)
            {
                var oldPath = Path.Combine(_fileCommandDirectory, fileCommand.FilePrefix + oldSuffix);
                if (oldSuffix != String.Empty && File.Exists(oldPath))
                {
                    TryDeleteFile(oldPath);
                }

                var newPath = Path.Combine(_fileCommandDirectory, fileCommand.FilePrefix + _fileSuffix);
                TryDeleteFile(newPath);
                File.Create(newPath).Dispose();

                // Give extensions a chance to populate the file before
                // the step starts (e.g., read-only views of job state).
                // Errors are logged but must not fail step setup.
                try
                {
                    fileCommand.PopulateInitialContents(context, newPath, container);
                }
                catch (Exception ex)
                {
                    _trace.Warning($"Failed to populate initial contents for file command '{fileCommand.ContextName}': {ex}");
                }

                var pathToSet = container != null ? container.TranslateToContainerPath(newPath) : newPath;
                context.SetGitHubContext(fileCommand.ContextName, pathToSet);
            }
        }

        public void ProcessFiles(IExecutionContext context, ContainerInfo container)
        {
            // Parse GITHUB_OUTPUT before generating the summary because the summary uses
            // the outputs written by actions/upload-artifact.
            foreach (var fileCommand in _commandExtensions)
            {
                if (fileCommand is CreateStepSummaryCommand)
                {
                    continue;
                }

                ProcessFileCommand(context, fileCommand, container);
            }

            foreach (var fileCommand in _commandExtensions)
            {
                if (fileCommand is CreateStepSummaryCommand)
                {
                    ProcessFileCommand(context, fileCommand, container);
                }
            }
        }

        private void ProcessFileCommand(IExecutionContext context, IFileCommandExtension fileCommand, ContainerInfo container)
        {
            try
            {
                fileCommand.ProcessCommand(context, Path.Combine(_fileCommandDirectory, fileCommand.FilePrefix + _fileSuffix), container);
            }
            catch (Exception ex)
            {
                context.Error($"Unable to process file command '{fileCommand.ContextName}' successfully.");
                context.Error(ex);
                context.CommandResult = TaskResult.Failed;
            }
        }

        private bool TryDeleteFile(string path)
        {
            if (!File.Exists(path))
            {
                return true;
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                _trace.Warning($"Unable to delete file {path} for reason: {e.ToString()}");
                return false;
            }
            return true;
        }
    }

    public interface IFileCommandExtension : IExtension
    {
        string ContextName { get; }
        string FilePrefix { get; }

        void ProcessCommand(IExecutionContext context, string filePath, ContainerInfo container);

        // Optional hook invoked by FileCommandManager.InitializeFiles
        // after creating the empty per-step file. Extensions that need to
        // pre-populate the file (e.g., a read-only view of job-scoped
        // state) override this; the default no-op preserves the existing
        // "empty file at start of step" behavior for write-only file
        // commands such as GITHUB_ENV, GITHUB_OUTPUT, GITHUB_PATH, etc.
        void PopulateInitialContents(IExecutionContext context, string filePath, ContainerInfo container) { }
    }

    public sealed class AddPathFileCommand : RunnerService, IFileCommandExtension
    {
        public string ContextName => "path";
        public string FilePrefix => "add_path_";

        public Type ExtensionType => typeof(IFileCommandExtension);

        public void ProcessCommand(IExecutionContext context, string filePath, ContainerInfo container)
        {
            if (File.Exists(filePath))
            {
                var lines = File.ReadAllLines(filePath, Encoding.UTF8);
                foreach (var line in lines)
                {
                    if (line == string.Empty)
                    {
                        continue;
                    }
                    if (context.DeferredPrependPath != null)
                    {
                        context.DeferredPrependPath.RemoveAll(x => string.Equals(x, line, StringComparison.CurrentCulture));
                        context.DeferredPrependPath.Add(line);
                    }
                    else
                    {
                        context.Global.PrependPath.RemoveAll(x => string.Equals(x, line, StringComparison.CurrentCulture));
                        context.Global.PrependPath.Add(line);
                    }
                }
            }
        }
    }

    public sealed class SetEnvFileCommand : RunnerService, IFileCommandExtension
    {
        public string ContextName => "env";
        public string FilePrefix => "set_env_";

        public Type ExtensionType => typeof(IFileCommandExtension);

        public void ProcessCommand(IExecutionContext context, string filePath, ContainerInfo container)
        {
            var pairs = new EnvFileKeyValuePairs(context, filePath);
            foreach (var pair in pairs)
            {
                var isBlocked = false;
                foreach (var blocked in _setEnvBlockList)
                {
                    if (string.Equals(blocked, pair.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        // Log Telemetry and let user know they shouldn't do this
                        var issue = new Issue()
                        {
                            Type = IssueType.Error,
                            Message = $"Can't store {blocked} output parameter using '$GITHUB_ENV' command."
                        };
                        issue.Data[Constants.Runner.InternalTelemetryIssueDataKey] = $"{Constants.Runner.UnsupportedCommand}_{pair.Key}";
                        context.AddIssue(issue, ExecutionContextLogOptions.Default);

                        isBlocked = true;
                        break;
                    }
                }
                if (isBlocked)
                {
                    continue;
                }
                SetEnvironmentVariable(context, pair.Key, pair.Value);
            }
        }

        private static void SetEnvironmentVariable(
            IExecutionContext context,
            string name,
            string value)
        {
            if (context.DeferredEnvironmentVariables != null)
            {
                context.DeferredEnvironmentVariables[name] = value;
            }
            else
            {
                context.Global.EnvironmentVariables[name] = value;
                context.SetEnvContext(name, value);
            }
            context.Debug($"{name}='{value}'");
        }

        private string[] _setEnvBlockList =
        {
            "NODE_OPTIONS"
        };
    }

    public sealed class CreateStepSummaryCommand : RunnerService, IFileCommandExtension
    {
        public const int AttachmentSizeLimit = 1024 * 1024;

        public string ContextName => "step_summary";
        public string FilePrefix => "step_summary_";

        public Type ExtensionType => typeof(IFileCommandExtension);

        private void AppendArtifactSummary(IExecutionContext context, string filePath, ContainerInfo container)
        {
            try
            {
                // External Results URL artifacts do not appear in GitHub's native artifact list.
                // Collect their rows here and publish one consolidated table when the job ends.
                var configuredResultsUrl = Environment.GetEnvironmentVariable("ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE");
                if (String.IsNullOrWhiteSpace(configuredResultsUrl) || !context.IsArtifactUploadAction)
                {
                    return;
                }

                if (!Uri.TryCreate(configuredResultsUrl, UriKind.Absolute, out var configuredUri)
                    || (configuredUri.Scheme != Uri.UriSchemeHttp && configuredUri.Scheme != Uri.UriSchemeHttps))
                {
                    Trace.Warning("ACTIONS_ARTIFACTS_RESULTS_URL_OVERRIDE is not a valid HTTP(S) URL; skipping external artifact summary table");
                    return;
                }

                var artifactName = "artifact";
                if (context.ActionInputs?.TryGetValue("name", out var configuredArtifactName) == true
                    && !String.IsNullOrWhiteSpace(configuredArtifactName))
                {
                    artifactName = configuredArtifactName.Trim();
                }
                else if (context.ActionInputs?.TryGetValue("artifactName", out configuredArtifactName) == true
                    && !String.IsNullOrWhiteSpace(configuredArtifactName))
                {
                    artifactName = configuredArtifactName.Trim();
                }

                var size = FormatArtifactSize(context.UploadedArtifactSizeBytes);
                var digest = ReadActionOutput(context, container, "artifact-digest");
                var artifactId = ReadActionOutput(context, container, "artifact-id");
                string downloadUrl = null;

                // actions/upload-artifact uses workflow_run_backend_id from the
                // Actions.Results scope in ACTIONS_RUNTIME_TOKEN. This is NOT the
                // public github.run_id. The standalone server stores files under a
                // directory key derived from this ID (see ToArtifactServerRunKey),
                // so the same key must be used to build a working download path.
                var workflowRunBackendId = ReadWorkflowRunBackendId(context);
                if (!String.IsNullOrWhiteSpace(workflowRunBackendId))
                {
                    var runKey = ToArtifactServerRunKey(workflowRunBackendId.Trim());
                    var relativeArtifactPath = $"{runKey}/{artifactName}/{artifactName}.zip";
                    var basePath = configuredUri.AbsolutePath.TrimEnd('/');
                    var downloadBaseUri = new UriBuilder(configuredUri)
                    {
                        Path = $"{basePath}/local-artifacts/download",
                        Query = String.Empty,
                        Fragment = String.Empty
                    }.Uri;
                    downloadUrl = $"{downloadBaseUri.GetLeftPart(UriPartial.Path)}?path={Uri.EscapeDataString(relativeArtifactPath)}";
                    Trace.Info($"Artifact summary download path key: '{relativeArtifactPath}'");
                }

                // Match the supplied GitHub-style card markup. Icon images use the
                // artifact server's SVG endpoint so they survive job-summary sanitizing.
                var packageIcon = BuildPackageOutlineIcon();
                var linkedPackageIcon = String.IsNullOrWhiteSpace(downloadUrl)
                    ? packageIcon
                    : $"<a href=\"{EscapeHtmlAttribute(downloadUrl)}\" style=\"color: #24292f; text-decoration: none; cursor: pointer;\">{packageIcon}</a>";
                var linkedName = String.IsNullOrWhiteSpace(downloadUrl)
                    ? EscapeHtmlText(artifactName)
                    : $"<a href=\"{EscapeHtmlAttribute(downloadUrl)}\" style=\"color: #24292f; text-decoration: none; cursor: pointer;\">{EscapeHtmlText(artifactName)}</a>";

                var digestHtml = String.IsNullOrWhiteSpace(digest)
                    ? "<span style=\"font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 13px;\">—</span>"
                    : $"<span style=\"font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 13px;\">{EscapeHtmlText(FormatDigestForDisplay(digest))}</span>";
                var copyIcon = String.IsNullOrWhiteSpace(digest)
                    ? String.Empty
                    : BuildIconImage(configuredUri, "copy", "Copy digest");

                var downloadIcon = BuildIconImage(configuredUri, "download", "Download");
                var linkedDownloadIcon = String.IsNullOrWhiteSpace(downloadUrl)
                    ? downloadIcon
                    : $"<a href=\"{EscapeHtmlAttribute(downloadUrl)}\" title=\"Download artifact\" style=\"cursor: pointer;\">{downloadIcon}</a>";
                var trashIcon = BuildIconImage(configuredUri, "trash", "Delete");

                var nameCell = $"<td style=\"padding: 16px; font-weight: 600;\"><div style=\"display: flex; align-items: center; gap: 8px;\">{linkedPackageIcon}{linkedName}</div></td>";
                var sizeCell = $"<td style=\"padding: 16px;\">{EscapeHtmlText(size)}</td>";
                var digestCell = $"<td style=\"padding: 16px;\"><div style=\"display: flex; align-items: center; gap: 8px;\">{digestHtml}{copyIcon}</div></td>";
                var downloadCell = $"<td style=\"padding: 16px; text-align: right; color: #57606a;\"><div style=\"display: flex; align-items: center; justify-content: flex-end; gap: 16px;\">{linkedDownloadIcon}{trashIcon}</div></td>";
                if (String.IsNullOrWhiteSpace(downloadUrl))
                {
                    Trace.Warning($"Could not determine the Actions.Results workflow-run backend ID for artifact '{artifactName}'; no download link was added");
                }

                var row = $"<tr style=\"border-top: 1px solid #d0d7de;\">{nameCell}{sizeCell}{digestCell}{downloadCell}</tr>";
                var rowKey = String.IsNullOrWhiteSpace(artifactId)
                    ? Guid.NewGuid().ToString("N")
                    : artifactId.Trim();

                ArtifactSummaryCollector.Add(context.Root.Id, context.Id, rowKey, row);
                Trace.Info($"Collected artifact summary row for '{artifactName}' (download link available: {!String.IsNullOrWhiteSpace(downloadCell)})");
            }
            catch (Exception ex)
            {
                // Summary rendering is best-effort and must not fail artifact uploads.
                Trace.Warning($"Could not collect artifact row for the job summary: {ex.Message}");
            }
        }

        private string ReadWorkflowRunBackendId(IExecutionContext context)
        {
            // The upload-artifact toolkit extracts these backend IDs from the
            // "Actions.Results:<workflow-run-backend-id>:<workflow-job-run-backend-id>"
            // scope in the runtime access token. Read only the run ID; never log the token.
            foreach (var endpoint in context.Global.Endpoints)
            {
                if (!String.Equals(endpoint.Name, "SystemVssConnection", StringComparison.OrdinalIgnoreCase)
                    || endpoint.Authorization?.Parameters == null
                    || !endpoint.Authorization.Parameters.TryGetValue("AccessToken", out var accessToken)
                    || String.IsNullOrWhiteSpace(accessToken))
                {
                    continue;
                }

                var tokenParts = accessToken.Split('.');
                if (tokenParts.Length < 2)
                {
                    return null;
                }

                try
                {
                    var payload = tokenParts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
                    using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                    if (!document.RootElement.TryGetProperty("scp", out var scopeClaim))
                    {
                        return null;
                    }

                    var scopes = scopeClaim.GetString();
                    if (String.IsNullOrWhiteSpace(scopes))
                    {
                        return null;
                    }

                    foreach (var scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = scope.Split(':');
                        if (parts.Length == 3 && String.Equals(parts[0], "Actions.Results", StringComparison.Ordinal))
                        {
                            return parts[1];
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.Warning($"Could not read Actions.Results IDs from the runtime token: {ex.Message}");
                    return null;
                }

                return null;
            }

            return null;
        }

        private static string ReadActionOutput(IExecutionContext context, ContainerInfo container, string outputName)
        {
            // The output file is processed before the summary command. Prefer the parsed step output.
            if (!String.IsNullOrEmpty(context.ContextName))
            {
                var scope = context.Global.StepsContext.GetScope(context.ScopeName);
                if (scope.TryGetValue(context.ContextName, out var stepValue))
                {
                    var step = stepValue.AssertDictionary("step");
                    if (step.TryGetValue("outputs", out var outputsValue))
                    {
                        var outputs = outputsValue.AssertDictionary("outputs");
                        if (outputs.TryGetValue(outputName, out var outputValue))
                        {
                            return outputValue as StringContextData;
                        }
                    }
                }
            }

            // Fallback to reading GITHUB_OUTPUT directly.
            var outputPath = context.GetGitHubContext("output");
            if (String.IsNullOrWhiteSpace(outputPath))
            {
                return null;
            }

            if (container != null)
            {
                outputPath = container.TranslateToHostPath(outputPath);
            }

            if (!File.Exists(outputPath))
            {
                return null;
            }

            var prefix = outputName + "=";
            foreach (var line in File.ReadLines(outputPath))
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length);
                }
            }

            return null;
        }

        private static string EscapeHtmlText(string value)
        {
            return (value ?? String.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        private static string EscapeHtmlAttribute(string value)
        {
            return EscapeHtmlText(value);
        }

        private static string FormatArtifactSize(long? sizeBytes)
        {
            if (!sizeBytes.HasValue || sizeBytes.Value < 0)
            {
                return "—";
            }

            var bytes = sizeBytes.Value;
            if (bytes >= 1024 * 1024)
            {
                return $"{Math.Round(bytes / (1024d * 1024d), 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture)} MB";
            }

            if (bytes >= 1024)
            {
                return $"{Math.Round(bytes / 1024d, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture)} KB";
            }

            return $"{bytes} B";
        }

        // GitHub shows the algorithm prefix plus the first 8 hex characters and an ellipsis.
        private static string FormatDigestForDisplay(string digest)
        {
            var trimmed = digest.Trim();
            var hexStart = trimmed.IndexOf(':') + 1;
            return trimmed.Length - hexStart > 8
                ? trimmed.Substring(0, hexStart + 8) + "…"
                : trimmed;
        }

        private static string BuildPackageOutlineIcon()
        {
            // Keep the cube hollow and fixed at 16px even when the surrounding table gets narrow.
            return "<svg width=\"16\" height=\"16\" viewBox=\"0 0 16 16\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\" style=\"color: #57606a; flex-shrink: 0; min-width: 16px;\"><path d=\"M1.75 4.75L8 1.5L14.25 4.75V11.25L8 14.5L1.75 11.25V4.75Z\" /><path d=\"M1.75 4.75L8 8L14.25 4.75\" /><path d=\"M8 8V14.5\" /></svg>";
        }

        private static string BuildIconImage(Uri configuredUri, string icon, string alt)
        {
            var iconUri = new UriBuilder(configuredUri)
            {
                Path = $"{configuredUri.AbsolutePath.TrimEnd('/')}/local-artifacts/icon/{icon}.svg",
                // GitHub's image proxy caches summary images by URL. Bump this value
                // whenever an icon changes, otherwise the old image keeps showing.
                Query = "v=2",
                Fragment = String.Empty
            }.Uri;
            return $"<img src=\"{EscapeHtmlAttribute(iconUri.AbsoluteUri)}\" alt=\"{EscapeHtmlAttribute(alt)}\" width=\"16\" height=\"16\" />";
        }

        // Mirrors the artifact server's directory key for a workflow_run_backend_id:
        // numeric IDs are used as-is, any other (opaque/GUID) ID is stored under the
        // signed 64-bit FNV-1a hash of the raw ID. Linking with the raw GUID gives a
        // dead download link because that directory does not exist.
        internal static string ToArtifactServerRunKey(string workflowRunBackendId)
        {
            if (long.TryParse(workflowRunBackendId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var numeric))
            {
                return numeric.ToString(CultureInfo.InvariantCulture);
            }

            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (var b in Encoding.UTF8.GetBytes(workflowRunBackendId))
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }

                return ((long)hash).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string EscapeMarkdownTableCell(string value)
        {
            return (value ?? String.Empty)
                .Replace("\\", "\\\\")
                .Replace("|", "\\|")
                .Replace("[", "\\[")
                .Replace("]", "\\]")
                .Replace("`", "\\`")
                .Replace("\r", " ")
                .Replace("\n", " ");
        }



        public void ProcessCommand(IExecutionContext context, string filePath, ContainerInfo container)
        {
            if (String.IsNullOrEmpty(filePath))
            {
                Trace.Info("Step Summary file path is empty; skipping attachment upload");
                return;
            }

            try
            {
                AppendArtifactSummary(context, filePath, container);

                if (!File.Exists(filePath))
                {
                    Trace.Info($"Step Summary file ({filePath}) does not exist; skipping attachment upload");
                    return;
                }

                var fileSize = new FileInfo(filePath).Length;
                if (fileSize == 0)
                {
                    Trace.Info($"Step Summary file ({filePath}) is empty; skipping attachment upload");
                    return;
                }

                if (fileSize > AttachmentSizeLimit)
                {
                    context.Error(String.Format(Constants.Runner.UnsupportedSummarySize, AttachmentSizeLimit / 1024, fileSize / 1024));
                    Trace.Info($"Step Summary file ({filePath}) is too large ({fileSize} bytes); skipping attachment upload");

                    return;
                }

                Trace.Verbose($"Step Summary file exists: {filePath} and has a file size of {fileSize} bytes");
                var scrubbedFilePath = filePath + "-scrubbed";

                using (var streamReader = new StreamReader(filePath))
                using (var streamWriter = new StreamWriter(scrubbedFilePath))
                {
                    string line;
                    while ((line = streamReader.ReadLine()) != null)
                    {
                        var maskedLine = HostContext.SecretMasker.MaskSecrets(line);
                        streamWriter.WriteLine(maskedLine);
                    }
                }

                var attachmentName = !context.IsEmbedded
                    ? context.Id.ToString()
                    : context.EmbeddedId.ToString();

                Trace.Info($"Queueing file ({filePath}) for attachment upload ({attachmentName})");
                // Attachments must be added to the parent context (job), not the current context (step)
                context.Root.QueueAttachFile(ChecksAttachmentType.StepSummary, attachmentName, scrubbedFilePath);

                // Dual upload the same files to Results Service
                context.Global.Variables.TryGetValue("system.github.results_endpoint", out string resultsReceiverEndpoint);
                if (resultsReceiverEndpoint != null)
                {
                    Trace.Info($"Queueing results file ({filePath}) for attachment upload ({attachmentName})");
                    var stepId = context.IsEmbedded ? context.EmbeddedId : context.Id;
                    // Attachments must be added to the parent context (job), not the current context (step)
                    context.Root.QueueSummaryFile(attachmentName, scrubbedFilePath, stepId);
                }
            }
            catch (Exception e)
            {
                Trace.Error($"Error while processing file ({filePath}): {e}");
                context.Error($"Failed to create step summary using 'GITHUB_STEP_SUMMARY': {e.Message}");
            }
        }
    }

    public sealed class SaveStateFileCommand : RunnerService, IFileCommandExtension
    {
        public string ContextName => "state";
        public string FilePrefix => "save_state_";

        public Type ExtensionType => typeof(IFileCommandExtension);

        public void ProcessCommand(IExecutionContext context, string filePath, ContainerInfo container)
        {
            var pairs = new EnvFileKeyValuePairs(context, filePath);
            foreach (var pair in pairs)
            {
                // Embedded steps (composite) keep track of the state at the root level
                if (context.IsEmbedded)
                {
                    var id = context.EmbeddedId;
                    if (!context.Root.EmbeddedIntraActionState.ContainsKey(id))
                    {
                        context.Root.EmbeddedIntraActionState[id] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    context.Root.EmbeddedIntraActionState[id][pair.Key] = pair.Value;
                }
                // Otherwise modify the ExecutionContext
                else
                {
                    context.IntraActionState[pair.Key] = pair.Value;
                }

                context.Debug($"Save intra-action state {pair.Key} = {pair.Value}");
            }
        }
    }

    public sealed class SetOutputFileCommand : RunnerService, IFileCommandExtension
    {
        public string ContextName => "output";
        public string FilePrefix => "set_output_";

        public Type ExtensionType => typeof(IFileCommandExtension);

        public void ProcessCommand(IExecutionContext context, string filePath, ContainerInfo container)
        {
            var pairs = new EnvFileKeyValuePairs(context, filePath);
            foreach (var pair in pairs)
            {
                if (context.DeferredOutputs != null)
                {
                    context.DeferredOutputs[pair.Key] = pair.Value;
                }
                else
                {
                    context.SetOutput(pair.Key, pair.Value, out var reference);
                }
                context.Debug($"Set output {pair.Key} = {pair.Value}");
            }
        }
    }

    public sealed class EnvFileKeyValuePairs : IEnumerable<KeyValuePair<string, string>>
    {
        private IExecutionContext _context;
        private string _filePath;

        public EnvFileKeyValuePairs(IExecutionContext context, string filePath)
        {
            _context = context;
            _filePath = filePath;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            var text = string.Empty;
            try
            {
                text = File.ReadAllText(_filePath) ?? string.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                _context.Debug($"File does not exist '{_filePath}'");
                yield break;
            }
            catch (FileNotFoundException)
            {
                _context.Debug($"File does not exist '{_filePath}'");
                yield break;
            }

            var index = 0;
            var line = ReadLine(text, ref index);
            while (line != null)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    var key = string.Empty;
                    var output = string.Empty;

                    var equalsIndex = line.IndexOf("=", StringComparison.Ordinal);
                    var heredocIndex = line.IndexOf("<<", StringComparison.Ordinal);

                    // Normal style NAME=VALUE
                    if (equalsIndex >= 0 && (heredocIndex < 0 || equalsIndex < heredocIndex))
                    {
                        var split = line.Split(new[] { '=' }, 2, StringSplitOptions.None);
                        if (string.IsNullOrEmpty(line))
                        {
                            throw new Exception($"Invalid format '{line}'. Name must not be empty");
                        }

                        key = split[0];
                        output = split[1];
                    }

                    // Heredoc style NAME<<EOF
                    else if (heredocIndex >= 0 && (equalsIndex < 0 || heredocIndex < equalsIndex))
                    {
                        var split = line.Split(new[] { "<<" }, 2, StringSplitOptions.None);
                        if (string.IsNullOrEmpty(split[0]) || string.IsNullOrEmpty(split[1]))
                        {
                            throw new Exception($"Invalid format '{line}'. Name must not be empty and delimiter must not be empty");
                        }
                        key = split[0];
                        var delimiter = split[1];
                        var startIndex = index; // Start index of the value (inclusive)
                        var endIndex = index;   // End index of the value (exclusive)
                        var tempLine = ReadLine(text, ref index, out var newline);
                        while (!string.Equals(tempLine, delimiter, StringComparison.Ordinal))
                        {
                            if (tempLine == null)
                            {
                                throw new Exception($"Invalid value. Matching delimiter not found '{delimiter}'");
                            }
                            if (newline == null)
                            {
                                throw new Exception($"Invalid value. EOF marker missing new line.");
                            }
                            endIndex = index - newline.Length;
                            tempLine = ReadLine(text, ref index, out newline);
                        }

                        output = endIndex > startIndex ? text.Substring(startIndex, endIndex - startIndex) : string.Empty;
                    }
                    else
                    {
                        throw new Exception($"Invalid format '{line}'");
                    }

                    yield return new KeyValuePair<string, string>(key, output);
                }

                line = ReadLine(text, ref index);
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
        {
            // Invoke IEnumerator<KeyValuePair<string, string>> GetEnumerator() above.
            return GetEnumerator();
        }

        private static string ReadLine(
                string text,
                ref int index)
        {
            return ReadLine(text, ref index, out _);
        }

        private static string ReadLine(
                string text,
                ref int index,
                out string newline)
        {
            if (index >= text.Length)
            {
                newline = null;
                return null;
            }

            var originalIndex = index;
            var lfIndex = text.IndexOf("\n", index, StringComparison.Ordinal);
            if (lfIndex < 0)
            {
                index = text.Length;
                newline = null;
                return text.Substring(originalIndex);
            }

#if OS_WINDOWS
            var crLFIndex = text.IndexOf("\r\n", index, StringComparison.Ordinal);
            if (crLFIndex >= 0 && crLFIndex < lfIndex)
            {
                index = crLFIndex + 2; // Skip over CRLF
                newline = "\r\n";
                return text.Substring(originalIndex, crLFIndex - originalIndex);
            }
#endif

            index = lfIndex + 1; // Skip over LF
            newline = "\n";
            return text.Substring(originalIndex, lfIndex - originalIndex);
        }
    }

    internal static class ArtifactSummaryCollector
    {
        private sealed class JobSummaryState
        {
            public object Gate { get; } = new object();
            public List<string> Rows { get; } = new List<string>();
            public HashSet<string> ArtifactIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            public Guid LastStepId { get; set; }
        }

        private static readonly ConcurrentDictionary<Guid, JobSummaryState> _jobs = new ConcurrentDictionary<Guid, JobSummaryState>();

        public static void Add(Guid jobId, Guid stepId, string artifactId, string markdownRow)
        {
            var state = _jobs.GetOrAdd(jobId, _ => new JobSummaryState());
            lock (state.Gate)
            {
                if (state.ArtifactIds.Add(artifactId))
                {
                    state.Rows.Add(markdownRow);
                }

                state.LastStepId = stepId;
            }
        }

        public static bool TryCreateSummary(Guid jobId, string tempDirectory, out string summaryPath, out Guid stepRecordId)
        {
            summaryPath = null;
            stepRecordId = Guid.Empty;

            if (!_jobs.TryGetValue(jobId, out var state))
            {
                return false;
            }

            string[] rows;
            lock (state.Gate)
            {
                rows = state.Rows.ToArray();
                stepRecordId = state.LastStepId;
            }

            if (rows.Length == 0 || stepRecordId == Guid.Empty)
            {
                return false;
            }

            Directory.CreateDirectory(tempDirectory);
            summaryPath = Path.Combine(tempDirectory, $"artifact-summary-{jobId:N}.md");

            var summary = new StringBuilder();
            summary.AppendLine("<div style=\"border: 1px solid #d0d7de; border-radius: 6px; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; background-color: #ffffff; color: #24292f; max-width: 900px;\">");
            summary.AppendLine("<div style=\"padding: 16px;\">");
            summary.AppendLine("<h2 style=\"margin: 0; font-size: 16px; font-weight: 600;\">Artifacts</h2>");
            summary.AppendLine("<p style=\"margin: 4px 0 24px 0; font-size: 14px; color: #57606a;\">Produced during runtime</p>");
            summary.AppendLine("<table style=\"width: 100%; border-collapse: collapse; text-align: left; font-size: 14px;\">");
            summary.AppendLine("<thead><tr>");
            summary.AppendLine("<th style=\"padding: 8px 16px 16px 16px; color: #57606a; font-weight: 600; width: 45%;\">Name</th>");
            summary.AppendLine("<th style=\"padding: 8px 16px 16px 16px; color: #57606a; font-weight: 600; width: 20%;\">Size</th>");
            summary.AppendLine("<th style=\"padding: 8px 16px 16px 16px; color: #57606a; font-weight: 600;\">Digest</th>");
            summary.AppendLine("<th style=\"padding: 8px 16px 16px 16px;\"></th>");
            summary.AppendLine("</tr></thead>");
            summary.AppendLine("<tbody>");
            foreach (var row in rows)
            {
                summary.AppendLine(row);
            }
            summary.AppendLine("</tbody>");
            summary.AppendLine("</table>");
            summary.AppendLine("</div>");
            summary.AppendLine("</div>");

            File.WriteAllText(summaryPath, summary.ToString(), new UTF8Encoding(false));
            _jobs.TryRemove(jobId, out _);
            return true;
        }
    }

}
