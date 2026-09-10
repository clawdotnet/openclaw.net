using System.Text.Json;
using OpenClaw.Core.Backup;

namespace OpenClaw.Cli;

internal static class BackupCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine("backup create <plan.json> <new-directory> --offline\nbackup validate <backup-directory>\nbackup restore <backup-directory> <new-isolated-directory>\nStop all writers before create. Plans must cover configuration, sessions, goals, schedules, governance, and secret references.");
            return 0;
        }
        switch (args[0])
        {
            case "create" when args.Length == 4 && args[3] == "--offline":
                var planPath = Path.GetFullPath(args[1]);
                var plan = JsonSerializer.Deserialize(await File.ReadAllTextAsync(planPath), BackupJsonContext.Default.InstanceBackupPlan)
                    ?? throw new InvalidDataException("Invalid backup plan.");
                foreach (var key in plan.Roots.Keys.ToArray()) plan.Roots[key] = Path.GetFullPath(plan.Roots[key], Path.GetDirectoryName(planPath)!);
                await InstanceBackup.CreateAsync(plan, args[2], offline: true);
                Console.WriteLine("Backup created and checksums validated."); return 0;
            case "validate" when args.Length == 2:
                var manifest = await InstanceBackup.ValidateAsync(args[1]);
                Console.WriteLine($"Validated {manifest.Files.Count} files. No instance was started."); return 0;
            case "restore" when args.Length == 3:
                await InstanceBackup.RestoreAsync(args[1], args[2]);
                Console.WriteLine("Restored and validated in isolation. Review paths and secret references before starting an instance."); return 0;
            default: throw new ArgumentException("Invalid backup command. Run: openclaw backup --help");
        }
    }
}
