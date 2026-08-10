using RDPCrystalEDILibrary;
using RDPCrystalEDILibrary.Rules;
using RDPCrystalRestService.Models;
using RDPCrystalRestService.Options;
using System.Text.RegularExpressions;

namespace RDPCrystalRestService.Services;

public sealed class RdpValidateService
{
    private readonly ILogger<RdpValidateService> _logger;
    private readonly CredentialsOptions _credentialsOptions;
    private string _rulesBasePath = string.Empty;

    public RdpValidateService(ILogger<RdpValidateService> logger, Microsoft.Extensions.Options.IOptions<CredentialsOptions> credentialsOptions)
    {
        _logger = logger;
        _credentialsOptions = credentialsOptions.Value;
    }

    public X12Response ProcessX12(
        string dataFile,
        string transaction,
        string? rulesPath,
        string? ruleFile,
        int snipLevel,
        string? ignoreSegments)
    {
        const int maxErrors = 999;
        _logger.LogInformation("In ProcessX12, file: {DataFile}", dataFile);

        if (!File.Exists(dataFile))
        {
            return new X12Response
            {
                dataFile = dataFile,
                validator = null,
                ackStr = "Data file does not exist: " + dataFile,
                status = "FAIL",
                ediRulesFile = "NA"
            };
        }

        try
        {
            string[]? segments = null;
            if (!string.IsNullOrWhiteSpace(ignoreSegments))
            {
                segments = ignoreSegments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }

            _rulesBasePath = rulesPath ?? string.Empty;

            PackageLicense.Key = GetLicense(_rulesBasePath, _credentialsOptions.Rdp.LicenseKey);

            if (string.IsNullOrWhiteSpace(ruleFile))
            {
                foreach (var con in X12Transactions.TransactionsArr)
                {
                    if (string.Equals(transaction, con.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(con.Rule))
                        {
                            ruleFile = con.Rule;
                        }

                        break;
                    }
                }
            }

            ruleFile = ruleFile ?? string.Empty; // Ensure non-null for Path.Combine

            var validator = new EDIValidator
            {
                AutoDetectDelimiters = true,
                MaxErrorsBeforeThrowingException = 5000,
                EDIRulesFile = Path.Combine(_rulesBasePath, ruleFile ?? string.Empty),
                EDIFile = dataFile
            };

            validator.OnCodeCondition += ediValidator_CustomCondition;

            var fileInfo = new FileInfo(dataFile);
            if (fileInfo.Length > 1_000_000)
            {
                validator.LoadValidatedData = false;
            }

            validator.Validate();

            if (validator.Passed)
            {
                return new X12Response
                {
                    dataFile = dataFile,
                    validator = validator,
                    ediRulesFile = validator.EDIRulesFile,
                    ackStr = CreateAck(validator),
                    status = "PASS"
                };
            }

            var snipCheck = snipLevel switch
            {
                2 => SnipTestLevel.Requirement,
                3 => SnipTestLevel.Balance,
                > 3 => SnipTestLevel.Situational,
                _ => SnipTestLevel.Integrity
            };

            if (validator.Errors.Count > maxErrors)
            {
                return new X12Response
                {
                    dataFile = dataFile,
                    validator = null,
                    ackStr = null,
                    status = "FAIL, Aborting. More than MAX_ERRORS errors: " + maxErrors,
                    ediRulesFile = validator.EDIRulesFile
                };
            }

            Span<EDIError> deleteErrors = new EDIError[maxErrors];
            int validationInd = 0;

            foreach (var error in validator.Errors)
            {
                if (validationInd >= maxErrors)
                {
                    break;
                }

                if (error.SnipLevel > snipCheck)
                {
                    deleteErrors[validationInd++] = error;
                }

                if (segments != null && segments.Contains(error.Segment, StringComparer.OrdinalIgnoreCase))
                {
                    deleteErrors[validationInd++] = error;
                }
            }

            foreach (var delInd in deleteErrors)
            {
                if (delInd != null)
                {
                    validator.Errors.Remove(delInd);
                }
            }

            return new X12Response
            {
                dataFile = dataFile,
                validator = validator,
                ackStr = CreateAck(validator),
                status = validator.Errors.Count == 0 ? "PASS" : "FAIL",
                ediRulesFile = validator.EDIRulesFile
            };
        }
        catch (Exception ex)
        {
            return new X12Response
            {
                dataFile = dataFile,
                validator = null,
                ackStr = ex.Message,
                status = "FAIL, Exception: " + ex.Message,
                ediRulesFile = "Invalid request"
            };
        }
    }

    private static string GetLicense(string rulesPath, string? configuredLicenseKey)
    {
        if (!string.IsNullOrWhiteSpace(configuredLicenseKey))
        {
            return configuredLicenseKey.Trim();
        }

        string filePath = Path.Combine(rulesPath, "RDPLicense.txt");
        string licenseKey = "";

        if (File.Exists(filePath))
        {
            licenseKey = File.ReadAllText(filePath).Trim();
        }

        return licenseKey;
    }

    private static string CreateAck(EDIValidator validator)
    {
        var ack = new Ack999Generator
        {
            PaddingChar = '0'
        };

        var doc997 = ack.Generate(validator);
        return doc997.GenerateEDIData();
    }

    private void ediValidator_CustomCondition(object sender, CodeConditionEventArgs e)
    {
        e.ConditionValid = false;

        if (e.CodeCondition.Id == "npiValidation")
        {
            if (SafeElementValue(e, 0) == "1P" && SafeElementValue(e, 7) == "XX")
            {
                string npiNum = SafeElementValue(e, 8);
                e.ConditionValid = !Util.NpiValidator.IsValidNpi(npiNum);
            }
        }
        else if (e.CodeCondition.Id == "prvValidation")
        {
            if (SafeElementValue(e, 1) == "HPI")
            {
                string npiNum = SafeElementValue(e, 2);
                e.ConditionValid = !Util.NpiValidator.IsValidNpi(npiNum);
            }
        }
        else if (e.CodeCondition.Id == "dtpValidation")
        {
            e.ConditionValid = !Util.DtpValidator.IsValid(SafeElementValue(e, 1), SafeElementValue(e, 2));
        }
        else if (e.CodeCondition.Id == "dobValidation")
        {
            if (SafeElementValue(e, 0) == "D8")
            {
                e.ConditionValid = !Util.DobValidator.IsValid(SafeElementValue(e, 1));
            }
        }
        else if (e.CodeCondition.Id == "refValidation")
        {
            string qualifier = SafeElementValue(e, 0);
            string value = SafeElementValue(e, 1);

            if (qualifier == "SY")
            {
                e.ConditionValid = !Util.SsnValidator.IsValid(value);
            }
            else if (qualifier == "EI" || qualifier == "TJ")
            {
                e.ConditionValid = !Util.EinValidator.IsValid(value);
            }
            else if (qualifier == "EA")
            {
                e.ConditionValid = !Regex.IsMatch(value, @"^[a-zA-Z0-9]+$");
            }
        }
        else if (e.CodeCondition.Id == "zipValidation")
        {
            string st = SafeElementValue(e, 1);
            string zip = SafeElementValue(e, 2);
            string csvPath = Path.Combine(_rulesBasePath, "Lookup", "States.csv");

            try
            {
                string? val = Util.CsvLookup.GetSecondValueByKey(csvPath, st);
                if (string.IsNullOrWhiteSpace(val))
                {
                    e.ConditionValid = true;
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Exception in zipValidation for {Path}", csvPath);
            }

            if (zip.Length > 0)
            {
                e.ConditionValid = !Regex.IsMatch(zip, @"^\d{5}(-\d{4})?$|^(\d{5}|\d{9})$");
            }
        }
        else if (e.CodeCondition.Id == "nmValidation")
        {
            string nm2 = SafeElementValue(e, 2);
            string nm3 = SafeElementValue(e, 3);

            // Last name (nm2): requires at least 2 characters.
            // Pass: WYGAL, O'DONNELL, LOYOLA-MANRIQUE, MORALES- MONTILLA, SMITH, JR
            // Fail: Jose2, J. P. Morgan, John  Smith
            // Allows optional spaces around punctuation like hyphen/comma/apostrophe/parentheses.
            string lastNamePattern = @"^[A-Za-z](?:[A-Za-z]|[(),'-]\s*)*(?:[A-Za-z)])(?: +[A-Za-z](?:[A-Za-z]|[(),'-]\s*)*(?:[A-Za-z)]))*$";

            // First name (nm3): allows single character (e.g. J, S, C).
            // Pass: J, JOHN, A GIRL TAMARA, O'NEILL, TENLEE(ORTHO)
            // Fail: Jose2, John  Smith
            string firstNamePattern = @"^[A-Za-z](?:[A-Za-z'()-]*[A-Za-z)])?(?: +[A-Za-z](?:[A-Za-z'()-]*[A-Za-z)])?)*$";

            bool nm2Match = nm2.Length == 0 || Regex.IsMatch(nm2, lastNamePattern);
            bool nm3Match = nm3.Length == 0 || Regex.IsMatch(nm3, firstNamePattern);

            if (nm2.Length > 0)
            {
                e.ConditionValid = !nm2Match;
            }

            if (nm3.Length > 0 && e.ConditionValid == false)
            {
                e.ConditionValid = !nm3Match;
            }

            _logger.LogInformation(
                "nmValidation debug: Segment={Segment}, Nm2={Nm2}, Nm3={Nm3}, Nm2Match={Nm2Match}, Nm3Match={Nm3Match}, ConditionValid={ConditionValid}",
                e.CurrentSegment?.ToString() ?? string.Empty,
                nm2,
                nm3,
                nm2Match,
                nm3Match,
                e.ConditionValid);
        }
        else if (e.CodeCondition.Id.StartsWith("valueFound-", StringComparison.Ordinal) ||
                 e.CodeCondition.Id.StartsWith("valueNotFound-", StringComparison.Ordinal))
        {
            bool isValueFoundCondition = e.CodeCondition.Id.StartsWith("valueFound-", StringComparison.Ordinal);
            HandleValueLookupCondition(e, isValueFoundCondition);
        }
        else if (e.CodeCondition.Id.StartsWith("ICDCodevalueFound-", StringComparison.Ordinal) ||
                 e.CodeCondition.Id.StartsWith("ICDCodevalueNotFound-", StringComparison.Ordinal))
        {
            bool isValueFoundCondition = e.CodeCondition.Id.StartsWith("ICDCodevalueFound-", StringComparison.Ordinal);
            HandleIcdCodeValueLookupCondition(e, isValueFoundCondition);
        }
        else if (e.CodeCondition.Id == "eqLookup" && e.CurrentSegment.Elements.Count > 2)
        {
            string eq2 = SafeElementValue(e, 2);
            string csvPath = Path.Combine(_rulesBasePath, "Lookup", "EQ-02.csv");

            if (!string.IsNullOrEmpty(eq2))
            {
                try
                {
                    string? val = Util.CsvLookup.GetSecondValueByKey(csvPath, eq2);
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        e.ConditionValid = true;
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Exception in EQ lookup for {Path}", csvPath);
                }
            }
        }
        else if (e.CodeCondition.Id == "shouldStartWithNumber")
        {
            bool val = Regex.IsMatch(SafeElementValue(e, 0), @"^\d.+$");
            e.ConditionValid = !val;
        }
        else if (e.CodeCondition.Id == "DuplicateCheck")
        {
            var duplicates = new HashSet<string>();
            if (e.CurrentSegment.Elements.Count > 6)
            {
                string sv7 = e.CurrentSegment.Elements[6]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sv7))
                {
                    Console.WriteLine($"Checking for duplicates in segment {e.CurrentSegment.Name} with value: {sv7}"); 
                }
                /*
                for (int i = 1; i < e.CurrentSegment.Elements.Count - 1; i++)
                {
                    string num = e.CurrentSegment.Elements[i]?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(num))
                    {
                        continue;
                    }
                    else if (!duplicates.Add(num))
                    {
                            e.ConditionValid = true;
                            return;                        
                    }
                }
                */
            }

        }
    }

    private void HandleValueLookupCondition(CodeConditionEventArgs e, bool isValueFoundCondition)
    {
        string[] condParts = e.CodeCondition.Id.Split('-');

        if (condParts.Length < 3 || condParts.Length > 4)
        {
            e.ConditionValid = true;
            return;
        }

        if (!int.TryParse(condParts[2], out int fieldNum) || fieldNum <= 0 || fieldNum > e.CurrentSegment.Elements.Count)
        {
            e.ConditionValid = true;
            return;
        }

        string lookupValue;

        try
        {
            if (condParts.Length == 3)
            {
                var element = e.CurrentSegment.Elements[fieldNum - 1];
                if (element == null)
                {
                    e.ConditionValid = true;
                    return;
                }

                lookupValue = element.ToString();
            }
            else
            {
                if (!int.TryParse(condParts[3], out int nestedFieldNum) || nestedFieldNum <= 0)
                {
                    e.ConditionValid = true;
                    return;
                }

                var element = e.CurrentSegment.Elements[fieldNum - 1];
                if (element?.Elements == null || nestedFieldNum > element.Elements.Count)
                {
                    e.ConditionValid = true;
                    return;
                }

                var nestedElement = element.Elements[nestedFieldNum - 1];
                if (nestedElement == null)
                {
                    e.ConditionValid = true;
                    return;
                }

                lookupValue = nestedElement.ToString();
            }
        }
        catch
        {
            e.ConditionValid = true;
            return;
        }

        string lookupFile = condParts[1];
        if (!lookupFile.Contains('.'))
        {
            lookupFile += ".txt";
        }

        string lookupPath = Path.Combine(_rulesBasePath, "Lookup", lookupFile);

        try
        {
            bool exists = Util.ValueLookup.ExistsInFile(lookupPath, lookupValue);
            e.ConditionValid = isValueFoundCondition ? exists : !exists;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Lookup exception for {Path}", lookupPath);
            e.ConditionValid = true;
        }
    }

    private void HandleIcdCodeValueLookupCondition(CodeConditionEventArgs e, bool isValueFoundCondition)
    {
        string[] condParts = e.CodeCondition.Id.Split('-');

        if (condParts.Length < 3)
        {
            e.ConditionValid = true;
            return;
        }

        string lookupFile = condParts[1];
        if (!lookupFile.Contains('.'))
        {
            lookupFile += ".txt";
        }

        if (!int.TryParse(condParts[2], out int nestedFieldNum) || nestedFieldNum <= 0)
        {
            e.ConditionValid = true;
            return;
        }

        string lookupPath = Path.Combine(_rulesBasePath, "Lookup", lookupFile);

        foreach (var element in e.CurrentSegment.Elements)
        {
            if (element?.Elements == null || nestedFieldNum > element.Elements.Count)
            {
                continue;
            }

            string lookupValue = element.Elements[nestedFieldNum - 1]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(lookupValue))
            {
                continue;
            }

            try
            {
                bool exists = Util.ValueLookup.ExistsInFile(lookupPath, lookupValue);
                e.ConditionValid = isValueFoundCondition ? exists : !exists;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "ICD lookup exception for {Path}", lookupPath);
                e.ConditionValid = true;
            }

            return;
        }

        e.ConditionValid = true;
    }

    private static string SafeElementValue(CodeConditionEventArgs e, int index)
    {
        if (e.CurrentSegment.Elements.Count <= index)
        {
            return string.Empty;
        }

        return e.CurrentSegment.Elements[index]?.ToString() ?? string.Empty;
    }
}
