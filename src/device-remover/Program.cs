using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Principal;
using Spectre.Console;

namespace DeviceRemover
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            // Set console to UTF-8 to support Cyrillic characters perfectly
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;

            if (args.Length == 0)
            {
                InteractiveMode();
                return;
            }

            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "disable":
                case "remove":
                    DisableCommand(args.AsSpan(1));
                    break;
                case "enable":
                case "restore":
                    EnableCommand(args.AsSpan(1));
                    break;
                case "status":
                    StatusCommand();
                    break;
                case "search":
                    SearchCommand(args.AsSpan(1));
                    break;
                case "alias":
                case "aliases":
                    AliasCommand(args.AsSpan(1));
                    break;
                case "profile":
                case "profiles":
                    ProfileCommand(args.AsSpan(1));
                    break;
                case "interactive":
                    InteractiveMode();
                    break;
                case "help":
                case "-h":
                case "--help":
                    PrintHelp();
                    break;
                default:
                    AnsiConsole.MarkupLine($"[bold red]Ошибка:[/] Неизвестная команда '{command}'.");
                    PrintHelp();
                    break;
            }
        }

        #region Admin elevation helper

        public static bool IsAdmin()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void RelaunchAsAdmin(string[] args)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                exePath = Environment.ProcessPath;
            }
            if (string.IsNullOrEmpty(exePath))
            {
                AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Не удалось определить путь к исполняемому файлу для повышения прав.");
                Environment.Exit(1);
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                UseShellExecute = true,
                Verb = "runas" // Windows UAC Elevation
            };

            try
            {
                Process.Start(psi);
                Environment.Exit(0);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                AnsiConsole.MarkupLine("[bold red]Отмена:[/] Для изменения состояния устройств требуются права администратора.");
                Environment.Exit(1);
            }
        }

        #endregion

        #region Interactive Mode (TUI)

        private static void InteractiveMode()
        {
            // Verify admin rights for interactive mode since users toggle states here
            if (!IsAdmin())
            {
                RelaunchAsAdmin(new[] { "interactive" });
                return;
            }

            while (true)
            {
                Console.Clear();
                AnsiConsole.Write(new FigletText("Device Remover").Color(Color.DeepSkyBlue1));
                AnsiConsole.MarkupLine("[grey]Утилита для быстрого включения/отключения устройств ноутбука[/]");
                AnsiConsole.MarkupLine("[grey]-------------------------------------------------------------[/]");

                var config = ConfigManager.LoadConfig();
                var devices = DeviceManager.EnumerateDevices();

                AnsiConsole.MarkupLine($"Активный профиль: [bold yellow]{config.ActiveProfile ?? "custom"}[/]");
                AnsiConsole.WriteLine();

                // Build menu options
                var options = new List<string>();
                
                options.Add("[bold cyan]--- Профили ---[/]");
                foreach (var profile in config.Profiles)
                {
                    string desc = profile.Value.Description != null ? $" ({profile.Value.Description})" : string.Empty;
                    options.Add($"Применить профиль: {profile.Key}{desc}");
                }

                options.Add(string.Empty);
                options.Add("[bold cyan]--- Ручное переключение устройств ---[/]");
                foreach (var alias in config.Aliases)
                {
                    var matched = devices.Where(d => DeviceManager.IsMatch(d, alias.Value)).ToList();
                    string statusStr;
                    
                    if (matched.Count == 0)
                    {
                        statusStr = "[grey]Не найдено (Не подключено)[/]";
                    }
                    else
                    {
                        bool allEnabled = matched.All(m => m.IsEnabled);
                        bool allDisabled = matched.All(m => !m.IsEnabled);
                        if (allEnabled)
                            statusStr = "[green]ВКЛЮЧЕНО[/]";
                        else if (allDisabled)
                            statusStr = "[red]ОТКЛЮЧЕНО[/]";
                        else
                            statusStr = "[yellow]ЧАСТИЧНО[/]";
                    }

                    string desc = alias.Value.Description != null ? $" ({alias.Value.Description})" : string.Empty;
                    options.Add($"Переключить {alias.Key}{desc} -> {statusStr}");
                }

                options.Add(string.Empty);
                options.Add("[bold red]Выход[/]");

                // Display prompt
                var selected = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Выберите действие:")
                        .PageSize(15)
                        .AddChoices(options.Where(o => !string.IsNullOrEmpty(o)))
                );

                if (selected.Contains("---") || string.IsNullOrEmpty(selected))
                {
                    continue; // Header selected
                }

                if (selected.Contains("Выход"))
                {
                    break;
                }

                if (selected.StartsWith("Применить профиль: "))
                {
                    // Parse profile name (everything after "Применить профиль: " until first space or end of string)
                    string profilePart = selected.Substring("Применить профиль: ".Length);
                    string profileName = profilePart.Split(' ')[0];

                    ApplyProfileInternal(profileName, config);
                }
                else if (selected.StartsWith("Переключить "))
                {
                    // Parse alias name (everything after "Переключить " until first space)
                    string aliasPart = selected.Substring("Переключить ".Length);
                    string aliasName = aliasPart.Split(' ')[0];

                    ToggleAliasInternal(aliasName, config, devices);
                }
            }
        }

        private static void ApplyProfileInternal(string profileName, AppConfig config)
        {
            if (!config.Profiles.TryGetValue(profileName, out var profile))
            {
                AnsiConsole.MarkupLine($"[red]Профиль '{profileName}' не найден.[/]");
                Console.ReadKey(true);
                return;
            }

            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start($"Применение профиля [yellow]{profileName}[/]...", ctx =>
                {
                    var devices = DeviceManager.EnumerateDevices();
                    bool rebootRequired = false;
                    int successCount = 0;

                    foreach (var devConfig in profile.Devices)
                    {
                        string aliasName = devConfig.Key;
                        bool targetEnable = string.Equals(devConfig.Value, "Enabled", StringComparison.OrdinalIgnoreCase);

                        if (config.Aliases.TryGetValue(aliasName, out var alias))
                        {
                            var matched = devices.Where(d => DeviceManager.IsMatch(d, alias)).ToList();
                            foreach (var dev in matched)
                            {
                                if (dev.IsEnabled != targetEnable)
                                {
                                    if (!targetEnable && !dev.IsDisableable)
                                    {
                                        continue; // System doesn't allow disabling
                                    }

                                    DeviceManager.ChangeDeviceState(dev.InstanceId, targetEnable, out bool reqReboot);
                                    rebootRequired |= reqReboot;
                                    successCount++;
                                }
                            }
                        }
                    }

                    config.ActiveProfile = profileName;
                    ConfigManager.SaveConfig(config);

                    AnsiConsole.MarkupLine($"[green]Профиль [bold]{profileName}[/] успешно применен! Изменено устройств: {successCount}.[/]");
                    if (rebootRequired)
                    {
                        AnsiConsole.MarkupLine("[bold yellow]Внимание:[/] Для некоторых устройств требуется перезагрузка.");
                    }
                    
                    System.Threading.Thread.Sleep(1200);
                });
        }

        private static void ToggleAliasInternal(string aliasName, AppConfig config, List<DeviceInfo> devices)
        {
            if (!config.Aliases.TryGetValue(aliasName, out var alias))
            {
                return;
            }

            var matched = devices.Where(d => DeviceManager.IsMatch(d, alias)).ToList();
            if (matched.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Устройства не подключены или не найдены.[/]");
                Console.ReadKey(true);
                return;
            }

            // Decide target state: if any device is currently enabled, target state is disabled (toggle off)
            bool targetEnable = !matched.Any(m => m.IsEnabled);

            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start($"{(targetEnable ? "Включение" : "Отключение")} [yellow]{aliasName}[/]...", ctx =>
                {
                    bool rebootRequired = false;
                    int successCount = 0;

                    foreach (var dev in matched)
                    {
                        if (dev.IsEnabled != targetEnable)
                        {
                            if (!targetEnable && !dev.IsDisableable)
                            {
                                AnsiConsole.MarkupLine($"[yellow]Предупреждение:[/] Устройство [bold]{dev.FriendlyName}[/] не может быть отключено.");
                                continue;
                            }

                            DeviceManager.ChangeDeviceState(dev.InstanceId, targetEnable, out bool reqReboot);
                            rebootRequired |= reqReboot;
                            successCount++;
                        }
                    }

                    config.ActiveProfile = "custom";
                    ConfigManager.SaveConfig(config);

                    AnsiConsole.MarkupLine($"[green]Устройства для '{aliasName}' {(targetEnable ? "включены" : "отключены")}. Изменено: {successCount}.[/]");
                    if (rebootRequired)
                    {
                        AnsiConsole.MarkupLine("[bold yellow]Внимание:[/] Для некоторых устройств требуется перезагрузка.");
                    }

                    System.Threading.Thread.Sleep(1200);
                });
        }

        #endregion

        #region CLI Commands

        private static void DisableCommand(ReadOnlySpan<string> args)
        {
            ToggleDevicesCli(args, enable: false);
        }

        private static void EnableCommand(ReadOnlySpan<string> args)
        {
            ToggleDevicesCli(args, enable: true);
        }

        private static void ToggleDevicesCli(ReadOnlySpan<string> args, bool enable)
        {
            var aliasesToToggle = new List<string>();

            if (args.Length == 0)
            {
                // Interactive prompt mode, one by one until empty string
                AnsiConsole.MarkupLine($"[yellow]Ввод алиасов для {(enable ? "включения" : "отключения")} (пустая строка для завершения):[/]");
                int count = 1;
                while (true)
                {
                    string input = AnsiConsole.Ask<string>($"Device{count}:").Trim();
                    if (string.IsNullOrEmpty(input))
                    {
                        break;
                    }
                    
                    // Support comma separated input
                    var split = input.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
                    aliasesToToggle.AddRange(split);
                    count++;
                }
            }
            else
            {
                // Join arguments and split by comma to support both: "dr remove a, b" and "dr remove a b"
                string joined = string.Join(" ", args.ToArray());
                var split = joined.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim());
                aliasesToToggle.AddRange(split);
            }

            if (aliasesToToggle.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Не указано ни одного алиаса.[/]");
                return;
            }

            // Check admin
            if (!IsAdmin())
            {
                var relaunchArgs = new List<string> { enable ? "enable" : "disable" };
                relaunchArgs.AddRange(aliasesToToggle);
                RelaunchAsAdmin(relaunchArgs.ToArray());
                return;
            }

            var config = ConfigManager.LoadConfig();
            var devices = DeviceManager.EnumerateDevices();
            bool rebootRequired = false;

            foreach (var aliasName in aliasesToToggle)
            {
                if (!config.Aliases.TryGetValue(aliasName, out var alias))
                {
                    AnsiConsole.MarkupLine($"[bold red]Ошибка:[/] Алиас '{aliasName}' не найден в конфигурации.");
                    continue;
                }

                var matched = devices.Where(d => DeviceManager.IsMatch(d, alias)).ToList();
                if (matched.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]Предупреждение:[/] Устройства для алиаса '{aliasName}' не найдены в системе.");
                    continue;
                }

                foreach (var dev in matched)
                {
                    if (dev.IsEnabled != enable)
                    {
                        if (!enable && !dev.IsDisableable)
                        {
                            AnsiConsole.MarkupLine($"[yellow]Пропущено:[/] Устройство [bold]{dev.FriendlyName}[/] не может быть отключено системой.");
                            continue;
                        }

                        AnsiConsole.MarkupLine($"{(enable ? "Включение" : "Отключение")} [bold yellow]{dev.FriendlyName}[/]...");
                        if (DeviceManager.ChangeDeviceState(dev.InstanceId, enable, out bool reqReboot))
                        {
                            AnsiConsole.MarkupLine($"  [green]Успешно {(enable ? "включено" : "отключено")}.[/]");
                            rebootRequired |= reqReboot;
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("  [red]Ошибка при изменении состояния.[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"Устройство [grey]{dev.FriendlyName}[/] уже в целевом состоянии {(enable ? "включено" : "отключено")}.");
                    }
                }
            }

            // Reset active profile since manual toggling was performed
            config.ActiveProfile = "custom";
            ConfigManager.SaveConfig(config);

            if (rebootRequired)
            {
                AnsiConsole.MarkupLine("[bold yellow]Внимание:[/] Для применения изменений к некоторым устройствам требуется перезагрузка ПК.");
            }
        }

        private static void StatusCommand()
        {
            var config = ConfigManager.LoadConfig();
            
            AnsiConsole.MarkupLine($"Активный профиль: [bold yellow]{config.ActiveProfile ?? "custom"}[/]");
            AnsiConsole.MarkupLine($"Конфиг загружен из: [grey]{ConfigManager.GetConfigPath()}[/]");
            AnsiConsole.WriteLine();

            var table = new Table();
            table.AddColumn("Алиас");
            table.AddColumn("Описание в конфигурации");
            table.AddColumn("Правило поиска");
            table.AddColumn("Совпадения в системе");
            table.AddColumn("Статус");

            var devices = DeviceManager.EnumerateDevices();

            foreach (var kvp in config.Aliases)
            {
                string aliasName = kvp.Key;
                var aliasConfig = kvp.Value;
                
                var matched = devices.Where(d => DeviceManager.IsMatch(d, aliasConfig)).ToList();

                string ruleInfo = $"[grey]{aliasConfig.MatchField}[/] {aliasConfig.MatchMode}: [yellow]{aliasConfig.Value}[/]";
                string matchedNames = string.Empty;
                string statusStr;

                if (matched.Count == 0)
                {
                    matchedNames = "[grey]Не найдено (Не подключено)[/]";
                    statusStr = "[grey]НЕТ[/]";
                }
                else
                {
                    matchedNames = string.Join("\n", matched.Select(m => $"- {m.FriendlyName} [grey]({m.InstanceId})[/]"));
                    
                    bool allEnabled = matched.All(m => m.IsEnabled);
                    bool allDisabled = matched.All(m => !m.IsEnabled);

                    if (allEnabled)
                        statusStr = "[green]ВКЛЮЧЕНО[/]";
                    else if (allDisabled)
                        statusStr = "[red]ОТКЛЮЧЕНО[/]";
                    else
                        statusStr = "[yellow]ЧАСТИЧНО[/]";
                }

                table.AddRow(
                    new Markup($"[bold cyan]{aliasName}[/]"),
                    new Markup(aliasConfig.Description ?? "[grey]Без описания[/]"),
                    new Markup(ruleInfo),
                    new Markup(matchedNames),
                    new Markup(statusStr)
                );
            }

            AnsiConsole.Write(table);
        }

        private static void SearchCommand(ReadOnlySpan<string> args)
        {
            if (args.Length == 0)
            {
                AnsiConsole.MarkupLine("[bold red]Использование:[/] dr search <запрос>");
                return;
            }

            string query = string.Join(" ", args.ToArray()).Trim();
            AnsiConsole.MarkupLine($"Поиск устройств по запросу: [yellow]{query}[/]...");

            var devices = DeviceManager.EnumerateDevices();
            var matched = devices.Where(d => 
                d.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.InstanceId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.HardwareIds.Any(h => h.Contains(query, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            if (matched.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]Устройств по данному запросу не найдено.[/]");
                return;
            }

            var table = new Table();
            table.AddColumn("Имя устройства");
            table.AddColumn("Instance ID");
            table.AddColumn("Аппаратные ID (Hardware IDs)");
            table.AddColumn("Отключение");
            table.AddColumn("Статус");

            foreach (var dev in matched)
            {
                // Highlight matches in friendly name
                string friendlyHighlight = HighlightText(dev.FriendlyName, query);
                string instanceHighlight = HighlightText(dev.InstanceId, query);
                
                string hwIds = string.Join("\n", dev.HardwareIds.Select(h => HighlightText(h, query)));
                if (string.IsNullOrEmpty(hwIds)) hwIds = "[grey]Нет[/]";

                string disableable = dev.IsDisableable ? "[green]Да[/]" : "[red]Нет[/]";
                string status = dev.IsEnabled ? "[green]Включено[/]" : "[red]Отключено[/]";

                table.AddRow(
                    new Markup(friendlyHighlight),
                    new Markup(instanceHighlight),
                    new Markup(hwIds),
                    new Markup(disableable),
                    new Markup(status)
                );
            }

            AnsiConsole.Write(table);
        }

        private static string HighlightText(string text, string query)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return Markup.Escape(text);

            string before = Markup.Escape(text.Substring(0, idx));
            string match = text.Substring(idx, query.Length);
            string after = Markup.Escape(text.Substring(idx + query.Length));

            return $"{before}[bold green]{Markup.Escape(match)}[/]{after}";
        }

        private static void AliasCommand(ReadOnlySpan<string> args)
        {
            if (args.Length == 0)
            {
                PrintAliasHelp();
                return;
            }

            string subCommand = args[0].ToLowerInvariant();
            var config = ConfigManager.LoadConfig();

            switch (subCommand)
            {
                case "list":
                    PrintAliasList(config);
                    break;

                case "add":
                    if (args.Length < 3)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Недостаточно аргументов для добавления алиаса.");
                        AnsiConsole.MarkupLine("Использование: dr alias add <имя> <паттерн> [--field <поле>] [--mode <режим>] [--desc <описание>]");
                        return;
                    }
                    
                    string aliasName = args[1];
                    string pattern = args[2];
                    string field = "Any";
                    string mode = "Contains";
                    string? description = null;

                    for (int i = 3; i < args.Length; i++)
                    {
                        if ((string.Equals(args[i], "--field", StringComparison.OrdinalIgnoreCase) || string.Equals(args[i], "-f", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                            field = args[++i];
                        else if ((string.Equals(args[i], "--mode", StringComparison.OrdinalIgnoreCase) || string.Equals(args[i], "-m", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                            mode = args[++i];
                        else if ((string.Equals(args[i], "--desc", StringComparison.OrdinalIgnoreCase) || string.Equals(args[i], "-d", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                            description = args[++i];
                    }

                    config.Aliases[aliasName] = new AliasConfig
                    {
                        Value = pattern,
                        MatchField = field,
                        MatchMode = mode,
                        Description = description
                    };
                    ConfigManager.SaveConfig(config);
                    AnsiConsole.MarkupLine($"[green]Алиас [bold]{aliasName}[/] успешно добавлен/обновлен![/]");
                    break;

                case "remove":
                case "delete":
                    if (args.Length < 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Укажите имя алиаса для удаления.");
                        return;
                    }
                    string toRemove = args[1];
                    if (config.Aliases.Remove(toRemove))
                    {
                        // Clean up profiles referencing this alias
                        foreach (var profile in config.Profiles.Values)
                        {
                            profile.Devices.Remove(toRemove);
                        }

                        ConfigManager.SaveConfig(config);
                        AnsiConsole.MarkupLine($"[green]Алиас [bold]{toRemove}[/] успешно удален.[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Алиас '{toRemove}' не найден.[/]");
                    }
                    break;

                case "rename":
                    if (args.Length < 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Укажите старое и новое имя алиаса. Пример: dr alias rename old=new");
                        return;
                    }
                    
                    string renameArg = args[1];
                    string[] renameParts = renameArg.Split('=');
                    if (renameParts.Length != 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Неверный формат. Используйте old_alias=new_alias");
                        return;
                    }

                    string oldName = renameParts[0].Trim();
                    string newName = renameParts[1].Trim();

                    if (!config.Aliases.TryGetValue(oldName, out var aliasData))
                    {
                        AnsiConsole.MarkupLine($"[red]Алиас '{oldName}' не найден.[/]");
                        return;
                    }

                    config.Aliases.Remove(oldName);
                    config.Aliases[newName] = aliasData;

                    // Update profiles referencing this alias
                    foreach (var profile in config.Profiles.Values)
                    {
                        if (profile.Devices.TryGetValue(oldName, out string? state))
                        {
                            profile.Devices.Remove(oldName);
                            profile.Devices[newName] = state;
                        }
                    }

                    ConfigManager.SaveConfig(config);
                    AnsiConsole.MarkupLine($"[green]Алиас [bold]{oldName}[/] успешно переименован в [bold]{newName}[/].[/]");
                    break;

                default:
                    AnsiConsole.MarkupLine($"[bold red]Неизвестная подкоманда:[/] {subCommand}");
                    PrintAliasHelp();
                    break;
            }
        }

        private static void PrintAliasList(AppConfig config)
        {
            var table = new Table();
            table.AddColumn("Алиас");
            table.AddColumn("Описание");
            table.AddColumn("Поле поиска");
            table.AddColumn("Режим");
            table.AddColumn("Значение (Паттерн)");

            foreach (var alias in config.Aliases)
            {
                table.AddRow(
                    new Markup($"[bold cyan]{alias.Key}[/]"),
                    new Markup(alias.Value.Description ?? "[grey]Нет[/]"),
                    new Markup($"[yellow]{alias.Value.MatchField}[/]"),
                    new Markup(alias.Value.MatchMode),
                    new Markup(Markup.Escape(alias.Value.Value))
                );
            }

            AnsiConsole.Write(table);
        }

        private static void PrintAliasHelp()
        {
            AnsiConsole.MarkupLine("[bold cyan]Управление алиасами (dr alias):[/]");
            AnsiConsole.MarkupLine("  dr alias list                            - Список всех алиасов");
            AnsiConsole.MarkupLine("  dr alias add <имя> <паттерн> [опции]    - Добавить/обновить алиас");
            AnsiConsole.MarkupLine("  dr alias remove <имя>                    - Удалить алиас");
            AnsiConsole.MarkupLine("  dr alias rename <старое>=<новое>        - Переименовать алиас");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Опции добавления алиаса:[/]");
            AnsiConsole.MarkupLine("  -f, --field <поле>    - Поле для поиска: Any, InstanceId, HardwareId, FriendlyName (Дефолт: Any)");
            AnsiConsole.MarkupLine("  -m, --mode <режим>    - Режим поиска: Contains, Exact, Regex, Wildcard (Дефолт: Contains)");
            AnsiConsole.MarkupLine("  -d, --desc <описание> - Описание устройства");
        }

        private static void ProfileCommand(ReadOnlySpan<string> args)
        {
            if (args.Length == 0)
            {
                PrintProfileHelp();
                return;
            }

            string subCommand = args[0].ToLowerInvariant();
            var config = ConfigManager.LoadConfig();

            switch (subCommand)
            {
                case "list":
                    PrintProfileList(config);
                    break;

                case "active":
                    AnsiConsole.MarkupLine($"Активный профиль: [bold yellow]{config.ActiveProfile ?? "custom"}[/]");
                    break;

                case "apply":
                    if (args.Length < 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Укажите имя профиля для применения.");
                        return;
                    }
                    string profileToApply = args[1];
                    ApplyProfileInternal(profileToApply, config);
                    break;

                case "add":
                    if (args.Length < 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Укажите имя создаваемого профиля.");
                        return;
                    }
                    string newProfileName = args[1];
                    string? profileDesc = null;

                    if (args.Length >= 4 && (string.Equals(args[2], "--desc", StringComparison.OrdinalIgnoreCase) || string.Equals(args[2], "-d", StringComparison.OrdinalIgnoreCase)))
                    {
                        profileDesc = args[3];
                    }

                    config.Profiles[newProfileName] = new ProfileConfig
                    {
                        Description = profileDesc
                    };
                    ConfigManager.SaveConfig(config);
                    AnsiConsole.MarkupLine($"[green]Профиль [bold]{newProfileName}[/] успешно создан.[/]");
                    break;

                case "set":
                    if (args.Length < 3)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Недостаточно аргументов.");
                        AnsiConsole.MarkupLine("Использование: dr profile set <профиль> <алиас>=<Enabled|Disabled>");
                        return;
                    }
                    string profName = args[1];
                    string setArg = args[2];

                    if (!config.Profiles.TryGetValue(profName, out var targetProfile))
                    {
                        AnsiConsole.MarkupLine($"[red]Профиль '{profName}' не найден.[/]");
                        return;
                    }

                    string[] setParts = setArg.Split('=');
                    if (setParts.Length != 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Используйте формат alias=Enabled или alias=Disabled");
                        return;
                    }

                    string aliasKey = setParts[0].Trim();
                    string targetState = setParts[1].Trim();

                    if (!config.Aliases.ContainsKey(aliasKey))
                    {
                        AnsiConsole.MarkupLine($"[yellow]Предупреждение:[/] Алиас '{aliasKey}' не существует. Устройство добавлено в профиль, но не сработает пока алиас не будет создан.");
                    }

                    if (!string.Equals(targetState, "Enabled", StringComparison.OrdinalIgnoreCase) && 
                        !string.Equals(targetState, "Disabled", StringComparison.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Состояние должно быть 'Enabled' или 'Disabled'.");
                        return;
                    }

                    // Normalize targetState to CamelCase
                    targetState = string.Equals(targetState, "Enabled", StringComparison.OrdinalIgnoreCase) ? "Enabled" : "Disabled";

                    targetProfile.Devices[aliasKey] = targetState;
                    ConfigManager.SaveConfig(config);
                    AnsiConsole.MarkupLine($"[green]В профиле [bold]{profName}[/] установлено [bold]{aliasKey}={targetState}[/].[/]");
                    break;

                case "remove":
                case "delete":
                    if (args.Length < 2)
                    {
                        AnsiConsole.MarkupLine("[bold red]Ошибка:[/] Укажите имя профиля для удаления.");
                        return;
                    }
                    string toDel = args[1];
                    if (config.Profiles.Remove(toDel))
                    {
                        if (string.Equals(config.ActiveProfile, toDel, StringComparison.OrdinalIgnoreCase))
                        {
                            config.ActiveProfile = "custom";
                        }
                        ConfigManager.SaveConfig(config);
                        AnsiConsole.MarkupLine($"[green]Профиль [bold]{toDel}[/] удален.[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Профиль '{toDel}' не найден.[/]");
                    }
                    break;

                default:
                    AnsiConsole.MarkupLine($"[bold red]Неизвестная подкоманда:[/] {subCommand}");
                    PrintProfileHelp();
                    break;
            }
        }

        private static void PrintProfileList(AppConfig config)
        {
            var table = new Table();
            table.AddColumn("Профиль");
            table.AddColumn("Описание");
            table.AddColumn("Устройства и состояния");

            foreach (var profile in config.Profiles)
            {
                string devicesStr = string.Join("\n", profile.Value.Devices.Select(d => 
                    $"- {d.Key} -> {(string.Equals(d.Value, "Enabled", StringComparison.OrdinalIgnoreCase) ? "[green]Enabled[/]" : "[red]Disabled[/]")}"
                ));
                if (string.IsNullOrEmpty(devicesStr)) devicesStr = "[grey]Нет правил[/]";

                table.AddRow(
                    new Markup($"[bold cyan]{profile.Key}[/]"),
                    new Markup(profile.Value.Description ?? "[grey]Нет[/]"),
                    new Markup(devicesStr)
                );
            }

            AnsiConsole.Write(table);
        }

        private static void PrintProfileHelp()
        {
            AnsiConsole.MarkupLine("[bold cyan]Управление профилями (dr profile):[/]");
            AnsiConsole.MarkupLine("  dr profile list                                - Список всех профилей");
            AnsiConsole.MarkupLine("  dr profile active                              - Показать активный профиль");
            AnsiConsole.MarkupLine("  dr profile apply <имя>                          - Применить профиль");
            AnsiConsole.MarkupLine("  dr profile add <имя> [--desc <описание>]       - Создать профиль");
            AnsiConsole.MarkupLine("  dr profile set <имя> <алиас>=<Enabled|Disabled> - Задать состояние устройства");
            AnsiConsole.MarkupLine("  dr profile remove <имя>                        - Удалить профиль");
        }

        private static void PrintHelp()
        {
            AnsiConsole.Write(new FigletText("dr help").Color(Color.DeepSkyBlue1));
            AnsiConsole.MarkupLine("Использование: [bold]dr[/] <команда> [аргументы]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold cyan]Команды устройства:[/]");
            AnsiConsole.MarkupLine("  dr [или dr interactive]                  - Запустить интерактивное меню (TUI dashboard)");
            AnsiConsole.MarkupLine("  dr status                                - Показать текущие состояния настроенных устройств");
            AnsiConsole.MarkupLine("  dr search <запрос>                      - Поиск устройств в системе по имени/ID");
            AnsiConsole.MarkupLine("  dr disable [alias1,alias2...]            - Отключить указанные устройства (синоним: remove)");
            AnsiConsole.MarkupLine("  dr enable [alias1,alias2...]             - Включить указанные устройства (синоним: restore)");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold cyan]Команды конфигурации:[/]");
            AnsiConsole.MarkupLine("  dr alias [list/add/remove/rename] [опции] - Управление короткими именами устройств");
            AnsiConsole.MarkupLine("  dr profile [list/active/apply/add/set/remove] - Управление комплексными профилями");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey]Команды переключения состояния требуют прав администратора (автоматический UAC запрос)[/]");
        }

        #endregion
    }
}
