using System.Data;
using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace HCHB.FindLiveExecutionPlan.Cli;

internal static class Program
{
    private const string FilterTypeDatabase = "database";
    private const int TruncationLength = 80;
    private const int StandardSigIntExitCode = 130;
    private const int MsBetweenSpinnerFrames = 100;
    private const int MsBetweenQueryPolls = 10000;
    private static readonly CancellationTokenSource ApplicationCts = new();
    private static string executable;

    private static async Task Main(string[] args)
    {
        executable = Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()?.Location ?? "FindLiveExecutionPlan");

        Console.CancelKeyPress += CancelApplication;
        if (!ValidateArgs(args))
        {
            PrintUsageInstructions();
            return;
        }
        var (dbServer, database, queryContainsFilter) = ExtractArgs(args);
        var connectionString = GetConnectionString(dbServer, database);
        try
        {
            var firstTimeThrough = true;
            Console.WriteLine("Polling sp_WhoIsActive for query containing: " + queryContainsFilter);
            await using var connection = new SqlConnection(connectionString);
            bool hasFinalMatch;
            List<Activity> finalMatches;
            do
            {
                bool hasMatch;
                List<ActivityBase> interimMatches;
                DefaultTypeMap.MatchNamesWithUnderscores = true;
                var spinnerCts = CancellationTokenSource.CreateLinkedTokenSource(ApplicationCts.Token);
                var spinnerTask = ShowSpinner(spinnerCts.Token);
                do
                {
                    if (!firstTimeThrough)
                        try
                        {
                            await Task.Delay(MsBetweenQueryPolls, ApplicationCts.Token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                    firstTimeThrough = false;
                    var interimResults = await GetActivity<ActivityBase>(connection, database);
                    // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract lies!
                    interimMatches = GetInterimMatches(interimResults, queryContainsFilter);
                    hasMatch = interimMatches.Count != 0;
                } while (!hasMatch);
                await spinnerCts.CancelAsync();
                await spinnerTask;
                spinnerCts.Dispose();
                Console.WriteLine($"Match found: {GetPrintableSqlText(interimMatches)}");
                Console.WriteLine("Rerunning sp_WhoIsActive with get_plans=1...");
                var finalResults = await GetActivity<Activity>(connection, database, true);
                // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract lies!
                finalMatches = GetFinalMatches(finalResults, queryContainsFilter);
                // ReSharper enable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                hasFinalMatch = finalMatches.Count != 0;
                MaybePrintRerun(hasFinalMatch);
            } while (!hasFinalMatch);
            await SaveReports(finalMatches);
        }
        catch (Exception e)
        {
            Console.WriteLine(e.ToString());
        }
    }

    private static List<ActivityBase> GetInterimMatches(IEnumerable<ActivityBase> interimResults,
        string queryContainsFilter)
    {
        return interimResults.Where(x =>
            x.SqlText is not null &&
            x.SqlText.Contains(queryContainsFilter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void MaybePrintRerun(bool hasFinalMatch)
    {
        if (!hasFinalMatch)
            Console.WriteLine(
                "When rerunning sp_WhoIsActive with get_plans=1, no matches were found. Starting again...");
    }

    private static List<Activity> GetFinalMatches(IEnumerable<Activity> finalResults, string queryContainsFilter)
    {
        return finalResults.Where(x =>
            x.SqlText is not null && x.QueryPlan is not null &&
            x.SqlText.Contains(queryContainsFilter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string GetPrintableSqlText(List<ActivityBase> interimMatches)
    {
        var sqlText = interimMatches[0].SqlText;
        sqlText = sqlText.Length > TruncationLength ? sqlText[..TruncationLength] : sqlText;
        return sqlText;
    }

    private static (string, string, string) ExtractArgs(string[] args)
    {
        return (args[0], args[1], args[2]);
    }

    private static void PrintUsageInstructions()
    {
        // Write usage to stderr:
        Console.Error.WriteLine($"Usage: {executable} <dbServer> <database> <queryContainsFilter>");
    }

    private static bool ValidateArgs(string[] args)
    {
        return args.Length == 3;
    }

    private static async Task SaveReports(List<Activity> finalMatches)
    {
        var reportTime = DateTime.Now;
        for (var i = 0; i < finalMatches.Count; i++)
        {
            var match = finalMatches[i];
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            var matchId = $"{reportTime:yyyyMMddHHmmss}-{i:000}-{match.DatabaseName ?? "unknown"}-{match.SessionId}";
            var queryDetailsFileName = matchId + ".json";
            await File.WriteAllTextAsync(queryDetailsFileName,
                JsonSerializer.Serialize(match, new JsonSerializerOptions { WriteIndented = true }),
                ApplicationCts.Token);
            Console.WriteLine("Query details saved to: " + queryDetailsFileName);

            if (!string.IsNullOrWhiteSpace(match.QueryPlan))
            {
                var queryPlanFileName = matchId + ".sqlplan";
                await File.WriteAllTextAsync(queryPlanFileName, match.QueryPlan, ApplicationCts.Token);
                Console.WriteLine("Query plan saved to: " + queryPlanFileName);
            }
            else
            {
                Console.WriteLine("No query plan available for this match.");
            }
        }
    }

    private static async Task<IEnumerable<T>> GetActivity<T>(SqlConnection connection, string database,
        bool getPlans = false)
    {
        // TODO: Try get_plans = 2
        var sqlParameters = new { filter_type = FilterTypeDatabase, filter = database, get_plans = getPlans ? 1 : 0 };
        return await connection.QueryAsync<T>("master.dbo.sp_WhoIsActive", sqlParameters,
            commandType: CommandType.StoredProcedure);
    }

    private static string GetConnectionString(string dbServer, string database)
    {
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = dbServer,
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            CommandTimeout = 300
        }.ConnectionString;
        return connectionString;
    }

    private static async Task ShowSpinner(CancellationToken token)
    {
        var spinnerChars = new[] { '|', '/', '-', '\\' };
        var spinnerIndex = 0;

        while (!token.IsCancellationRequested)
        {
            Console.Write(spinnerChars[spinnerIndex]);
            spinnerIndex = (spinnerIndex + 1) % spinnerChars.Length;
            try
            {
                await Task.Delay(MsBetweenSpinnerFrames, token);
            }
            catch (TaskCanceledException)
            {
                // who cares?
            }
            Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
        }
    }

    private static void CancelApplication(object sender, ConsoleCancelEventArgs e)
    {
        Environment.ExitCode = StandardSigIntExitCode;
        // This property has a dumb name, but setting it true stops the default behavior of ctrl+c from killing
        // the process immediately after this event handler finishes.
        e.Cancel = true;
        ApplicationCts.Cancel();
    }
}