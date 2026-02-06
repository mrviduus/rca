using System.CommandLine;
using Rca.Commands;

var root = new RootCommand("RCA - Root Cause Analyzer for failed tests");
root.AddCommand(new AnalyzeCommand());

return await root.InvokeAsync(args);
