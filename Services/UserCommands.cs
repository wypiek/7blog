namespace SevenBlog.Services;

public static class UserCommands
{
    public static int Run(string[] args, UserStore users)
    {
        try
        {
            if (args.Length == 1 && args[0] == "list")
            {
                Console.WriteLine("Username\tRank\tStatus\tDisplay name");
                foreach (var user in users.List()) Console.WriteLine($"{user.Username}\t{user.Rank}\t{(user.Disabled ? "Disabled" : "Active")}\t{user.DisplayName}");
                return 0;
            }
            if (args.Length >= 2 && args[0] == "create")
            {
                var rank = "Author"; string? display = null; var claim = false;
                for (var i = 2; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--rank" when i + 1 < args.Length: rank = args[++i]; break;
                        case "--display-name" when i + 1 < args.Length: display = args[++i]; break;
                        case "--claim-legacy": claim = true; break;
                        default: throw new ArgumentException($"Unknown option: {args[i]}");
                    }
                }
                var password = ReadPassword();
                var user = users.Create(args[1], password, rank, display, claim);
                Console.WriteLine($"Created {user.Username} ({user.RankLabel})." + (claim ? " Existing unowned posts assigned to this account." : ""));
                ReportClaimed(users);
                return 0;
            }
            if (args.Length >= 2)
            {
                var user = users.FindByUsername(args[1]) ?? throw new ArgumentException("User not found.");
                switch (args[0])
                {
                    case "reset-password" when args.Length == 2: users.SetPassword(user, ReadPassword()); break;
                    case "set-rank" when args.Length == 3: users.SetRank(user, args[2]); break;
                    case "disable" when args.Length == 2: users.SetDisabled(user, true); break;
                    case "enable" when args.Length == 2: users.SetDisabled(user, false); break;
                    default: throw new ArgumentException("Unknown account command or arguments.");
                }
                Console.WriteLine($"Updated {user.Username}. Existing sessions have been invalidated.");
                ReportClaimed(users);
                return 0;
            }
            Console.Error.WriteLine("Usage: users create USERNAME [--rank Admin|Author] [--display-name NAME] [--claim-legacy]\n       users list\n       users reset-password USERNAME\n       users set-rank USERNAME Admin|Author\n       users disable|enable USERNAME");
            return 2;
        }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static void ReportClaimed(UserStore users)
    {
        var claimed = users.ClaimUnownedPosts();
        if (claimed > 0) Console.WriteLine($"Assigned {claimed} unowned {(claimed == 1 ? "post" : "posts")} to the first administrator.");
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? throw new ArgumentException("Provide a password on standard input.");
        string ReadSecret(string label)
        {
            Console.Write(label); var value = new System.Text.StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
                else if (!char.IsControl(key.KeyChar) && value.Length < 129) value.Append(key.KeyChar);
            }
            return value.ToString();
        }
        var password = ReadSecret("Password (12–128 characters): ");
        if (password != ReadSecret("Confirm password: ")) throw new ArgumentException("Passwords do not match.");
        return password;
    }
}
