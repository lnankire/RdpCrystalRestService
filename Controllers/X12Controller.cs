using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RDPCrystalEDILibrary;
using RDPCrystalRestService.Models;
using RDPCrystalRestService.Options;
using RDPCrystalRestService.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace RDPCrystalRestService.Controllers;

[ApiController]
[Route("X12")]
public sealed class X12Controller : ControllerBase
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> LogFileLocks = new(StringComparer.Ordinal);

    private readonly IrisDbService _irisService;
    private readonly RdpValidateService _validatorService;
    private readonly AppRuntimeOptions _runtimeOptions;
    private readonly ILogger<X12Controller> _logger;

    public X12Controller(
        IrisDbService irisService,
        RdpValidateService validatorService,
        IOptions<AppRuntimeOptions> runtimeOptions,
        ILogger<X12Controller> logger)
    {
        _irisService = irisService;
        _validatorService = validatorService;
        _runtimeOptions = runtimeOptions.Value;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string operation,
        [FromQuery] string? filePath,
        [FromQuery] string? fileName,
        [FromQuery] string? rulesPath,
        [FromQuery] string? rulesFile,
        [FromQuery] string? transaction,
        [FromQuery] string? sessionId,
        [FromQuery] int? snipLevel,
        [FromQuery] string? x12Id,
        [FromQuery] string? nameSpace,
        [FromQuery] string? ignoreSegments,
        [FromQuery] string? x12String,
        [FromQuery] int? logToDb,
        CancellationToken cancellationToken)
    {
        var requestTimer = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(operation))
        {
            return BadRequest(new { Results = "operation query parameter is required." });
        }

        int snipVal = snipLevel ?? 0;
        int logIris = logToDb ?? 1;

        string effectiveRulesPath = string.IsNullOrWhiteSpace(rulesPath) ? _runtimeOptions.DefaultRulesPath : rulesPath;
        string effectiveFilePath = string.IsNullOrWhiteSpace(filePath) ? _runtimeOptions.DefaultDataPath : filePath;

        if (string.IsNullOrWhiteSpace(effectiveRulesPath) || !Directory.Exists(effectiveRulesPath))
        {
            return BadRequest(new { Results = "Rules directory does not exist: " + effectiveRulesPath });
        }

        Directory.CreateDirectory(effectiveFilePath);

        try
        {
            if (string.Equals(operation, "RDP", StringComparison.OrdinalIgnoreCase))
            {
                return await ValidateSinglePayload(transaction, effectiveFilePath, filePath, fileName, effectiveRulesPath, rulesFile, snipVal, ignoreSegments, x12String, nameSpace, sessionId, logIris, cancellationToken);
            }

            if (string.Equals(operation, "IRIS", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(transaction) || string.IsNullOrWhiteSpace(x12Id))
                {
                    return BadRequest(new { Results = "transaction and x12Id are required for IRIS operation." });
                }

                var record = _irisService.GetAndPersistX12Data(nameSpace, x12Id, transaction, effectiveFilePath);
                if (record == null)
                {
                    return NotFound(new { Results = "X12 record not found." });
                }

                return Ok(new { Results = $"{record.Transaction} X12 data, ID: {x12Id} is stored in {record.FilePath}" });
            }

            if (string.Equals(operation, "Validate", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(transaction))
                {
                    return BadRequest(new { Results = "transaction is required for Validate operation." });
                }

                if (!string.IsNullOrWhiteSpace(x12Id))
                {
                    var record = _irisService.GetAndPersistX12Data(nameSpace, x12Id, transaction, effectiveFilePath);
                    if (record == null)
                    {
                        return NotFound(new { Transaction = transaction, ID = x12Id, Status = "FAIL, ID not found in IRIS" });
                    }

                    X12Response rdpResp = _validatorService.ProcessX12(record.FilePath, record.Transaction, effectiveRulesPath, rulesFile, snipVal, ignoreSegments);
                    string details = string.Empty;
                    if (rdpResp.validator != null)
                    {
                        details = _irisService.RdpErrorsToIris(nameSpace, rdpResp.validator, x12Id, sessionId ?? record.SessionId, rdpResp.ackStr, logIris);
                    }

                    var response = new { Transaction = record.Transaction, FileName = record.FilePath, Status = rdpResp.status, Details = details, ACK = Truncate(rdpResp.ackStr) };
                    if (rdpResp.status?.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return ValidationFailure(response);
                    }

                    return Ok(response);
                }

                int processed = _irisService.ProcessX12Table(nameSpace, transaction, effectiveFilePath, effectiveRulesPath, rulesFile, snipVal, _validatorService);
                return Ok(new { Results = $"{processed} files processed of transaction type: {transaction}" });
            }

            return BadRequest(new { Results = "Unsupported operation: " + operation });
        }
        catch (ArgumentException ex)
        {
            string logDirectory = string.IsNullOrWhiteSpace(_runtimeOptions.DefaultX12InputLogPath)
                ? effectiveFilePath
                : _runtimeOptions.DefaultX12InputLogPath;
            string logFile = Path.Combine(logDirectory, "Rdpx12Validate-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
            await AppendCombinedLogEntryAsync(
                logFile,
                sessionId,
                transaction,
                "operation=" + operation,
                "FAIL_ARGUMENT_EXCEPTION",
                fileName ?? string.Empty,
                requestTimer.ElapsedMilliseconds,
                ex.Message,
                cancellationToken);
            return BadRequest(new { Results = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in X12Controller GET.");
            string logDirectory = string.IsNullOrWhiteSpace(_runtimeOptions.DefaultX12InputLogPath)
                ? effectiveFilePath
                : _runtimeOptions.DefaultX12InputLogPath;
            string logFile = Path.Combine(logDirectory, "Rdpx12Validate-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
            await AppendCombinedLogEntryAsync(
                logFile,
                sessionId,
                transaction,
                "operation=" + operation,
                "FAIL_EXCEPTION_" + ex.GetType().Name,
                fileName ?? string.Empty,
                requestTimer.ElapsedMilliseconds,
                ex.Message,
                cancellationToken);
            return StatusCode(StatusCodes.Status500InternalServerError, new { Results = "Internal server error." });
        }
    }

    [HttpPost]
    public IActionResult Post([FromBody] dynamic data)
    {
        return Ok(new { you_sent = data });
    }

    private async Task<IActionResult> ValidateSinglePayload(
        string? transaction,
        string effectiveFilePath,
        string? requestedFilePath,
        string? fileName,
        string rulesPath,
        string? rulesFile,
        int snipVal,
        string? ignoreSegments,
        string? x12String,
        string? nameSpace,
        string? sessionId,
        int logIris,
        CancellationToken cancellationToken)
    {
        var requestTimer = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(transaction))
        {
            return BadRequest(new { Results = "transaction is required for RDP operation." });
        }

        string logDirectory = string.IsNullOrWhiteSpace(_runtimeOptions.DefaultX12InputLogPath)
            ? effectiveFilePath
            : _runtimeOptions.DefaultX12InputLogPath;

        Directory.CreateDirectory(logDirectory);
        string logFile = Path.Combine(logDirectory, "Rdpx12Validate-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");

        string requestInput = !string.IsNullOrWhiteSpace(fileName)
            ? "fileName=" + fileName
            : (!string.IsNullOrWhiteSpace(x12String) ? "x12StringLength=" + x12String.Length : "no-input");

        string validationFile = fileName ?? string.Empty;
        string resultStatus;

        try
        {
            if (string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(x12String))
            {
                if (x12String.Length < 108)
                {
                    resultStatus = "FAIL_BAD_REQUEST";
                    await AppendCombinedLogEntryAsync(logFile, sessionId, transaction, requestInput, resultStatus, string.Empty, requestTimer.ElapsedMilliseconds, "X12 string cannot be less than 108 bytes.", cancellationToken);
                    return BadRequest(new { Results = "X12 string cannot be less than 108 bytes." });
                }

                string writeBasePath = effectiveFilePath;
                Directory.CreateDirectory(writeBasePath);
                validationFile = Path.Combine(writeBasePath, "x12-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".txt");
                if (_runtimeOptions.WriteX12InputToDisk)
                {
                    await System.IO.File.WriteAllTextAsync(validationFile, x12String, Encoding.UTF8, cancellationToken);
                }
                else
                {
                    resultStatus = "FAIL_BAD_REQUEST";
                    await AppendCombinedLogEntryAsync(logFile, sessionId, transaction, requestInput, resultStatus, validationFile, requestTimer.ElapsedMilliseconds, "WriteX12InputToDisk=false requires fileName input.", cancellationToken);
                    return BadRequest(new { Results = "WriteX12InputToDisk=false requires fileName input." });
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    resultStatus = "FAIL_BAD_REQUEST";
                    await AppendCombinedLogEntryAsync(logFile, sessionId, transaction, requestInput, resultStatus, string.Empty, requestTimer.ElapsedMilliseconds, "fileName or x12String is required for RDP operation.", cancellationToken);
                    return BadRequest(new { Results = "fileName or x12String is required for RDP operation." });
                }

                Directory.CreateDirectory(effectiveFilePath);
                validationFile = Path.IsPathRooted(fileName) ? fileName : Path.Combine(effectiveFilePath, fileName);
            }

            if (!System.IO.File.Exists(validationFile))
            {
                resultStatus = "FAIL_NOT_FOUND";
                await AppendCombinedLogEntryAsync(logFile, sessionId, transaction, requestInput, resultStatus, validationFile, requestTimer.ElapsedMilliseconds, "Data file does not exist: " + validationFile, cancellationToken);
                return NotFound(new { Results = "Data file does not exist: " + validationFile });
            }

            X12Response rdpResp = _validatorService.ProcessX12(validationFile, transaction, rulesPath, rulesFile, snipVal, ignoreSegments);
            string dbWriteResult = string.Empty;

            if (rdpResp.validator != null)
            {
                string x12Id = Path.GetFileName(fileName ?? validationFile) ?? Path.GetFileName(validationFile);
                dbWriteResult = _irisService.RdpErrorsToIris(nameSpace, rdpResp.validator, x12Id, sessionId ?? string.Empty, rdpResp.ackStr ?? string.Empty, logIris);
            }

            string details = BuildDetails(rdpResp.validator, dbWriteResult);

            var response = new
            {
                Transaction = transaction ?? string.Empty,
                FileName = fileName ?? validationFile,
                Status = rdpResp.status ?? string.Empty,
                Details = details,
                ACK = Truncate(rdpResp.ackStr)
            };

            resultStatus = rdpResp.status ?? string.Empty;
            await AppendCombinedLogEntryAsync(logFile, sessionId, transaction, requestInput, resultStatus, validationFile, requestTimer.ElapsedMilliseconds, details, cancellationToken);

            if (rdpResp.status?.StartsWith("FAIL", StringComparison.OrdinalIgnoreCase) == true)
            {
                return ValidationFailure(response);
            }

            return Ok(response);
        }
        catch (Exception ex)
        {
            resultStatus = "FAIL_EXCEPTION_" + ex.GetType().Name;
            await AppendCombinedLogEntryAsync(logFile, sessionId, transaction, requestInput, resultStatus, validationFile, requestTimer.ElapsedMilliseconds, ex.Message, cancellationToken);
            throw;
        }
    }

    private async Task AppendCombinedLogEntryAsync(
        string logFile,
        string? sessionId,
        string? transaction,
        string requestInput,
        string resultStatus,
        string validationFile,
        long elapsedMs,
        string? details,
        CancellationToken cancellationToken)
    {
        try
        {
            static string Clean(string? value)
            {
                return (value ?? string.Empty)
                    .Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal)
                    .Trim();
            }

            string combinedEntry = "### REQUEST_RESULT | Utc=" + DateTime.UtcNow.ToString("O") +
                                   " | SessionId=" + Clean(sessionId) +
                                   " | Transaction=" + Clean(transaction) +
                                   " | Input=" + Clean(requestInput) +
                                   " | Status=" + Clean(resultStatus) +
                                   " | ElapsedMs=" + elapsedMs +
                                   " | File=" + Clean(validationFile);

            if (!string.Equals(Clean(resultStatus), "PASS", StringComparison.OrdinalIgnoreCase))
            {
                combinedEntry += " | Details=" + Clean(details);
            }

            var fileLock = LogFileLocks.GetOrAdd(logFile, static _ => new SemaphoreSlim(1, 1));
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                await using var stream = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteLineAsync(combinedEntry.TrimEnd('\r', '\n'));
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append combined RDP log entry to {LogFile}", logFile);
        }
    }

    private string BuildDetails(EDIValidator? validator, string dbWriteResult)
    {
        if (validator == null)
        {
            return Truncate(dbWriteResult);
        }

        var sb = new StringBuilder();
        sb.Append("Number of validation errors: ").Append(validator.Errors.Count).Append(';');

        foreach (EDIError error in validator.Errors)
        {
            sb.Append("Line: ").Append(error)
            .Append(", Transaction: ").Append(error.STSegment)
              .Append(", ValidatingSegment: ").Append(error.ValidatingSegment)
              .Append(", Loop: ").Append(error.Loop)
              .Append(", Segment: ").Append(error.Segment)
              .Append(", Ordinal: ").Append(error.ElementOrdinal)
              .Append(", Details: ").Append(error.Message)
              .Append(", ").Append(error.Description)
              .Append(';');
        }

        if (!string.IsNullOrWhiteSpace(dbWriteResult))
        {
            sb.Append("IRIS Logging: ").Append(dbWriteResult).Append(';');
        }

        return Truncate(sb.ToString());
    }

    private string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= _runtimeOptions.MaxResponseDetailsLength)
        {
            return value;
        }

        return value.Substring(0, _runtimeOptions.MaxResponseDetailsLength);
    }

    private IActionResult ValidationFailure(object response)
    {
        if (_runtimeOptions.ReturnValidationFailuresAs200)
        {
            return Ok(response);
        }

        return UnprocessableEntity(response);
    }
}
