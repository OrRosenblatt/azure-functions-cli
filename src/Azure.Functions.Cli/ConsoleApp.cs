﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Azure.Functions.Cli.Actions;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Extensions;
using Azure.Functions.Cli.Interfaces;
using Colors.Net;
using static Azure.Functions.Cli.Common.OutputTheme;


namespace Azure.Functions.Cli
{
    class ConsoleApp
    {
        private readonly IContainer _container;
        private readonly string[] _args;
        private readonly IEnumerable<TypeAttributePair> _actionAttributes;
        private readonly string[] _helpArgs = new[] { "help", "h", "?", "version", "v" };

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter")]
        public static void Run<T>(string[] args, IContainer container)
        {
            Task.Run(() => RunAsync<T>(args, container)).Wait();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1004:GenericMethodsShouldProvideTypeParameter")]
        public static async Task RunAsync<T>(string[] args, IContainer container)
        {
            var app = new ConsoleApp(args, typeof(T).Assembly, container);
            var action = app.Parse();
            if (action != null)
            {
                try
                {
                    await action.RunAsync();
                }
                catch (Exception ex)
                {
                    if (Environment.GetEnvironmentVariable(Constants.CliDebug) == "1")
                    {
                        ColoredConsole.Error.WriteLine(ErrorColor(ex.ToString()));
                    }
                    else
                    {
                        ColoredConsole.Error.WriteLine(ErrorColor(ex.Message));
                    }
                }
            }
        }

        public static bool RelaunchSelfElevated(IAction action, out string errors)
        {
            errors = string.Empty;
            var attribute = action.GetType().GetCustomAttribute<ActionAttribute>();
            if (attribute != null)
            {
                Func<Context, string> getContext = c => c == Context.None ? string.Empty : c.ToString();
                var context = getContext(attribute.Context);
                var subContext = getContext(attribute.Context);
                var name = attribute.Name;
                var args = action
                    .ParseArgs(Array.Empty<string>())
                    .UnMatchedOptions
                    .Select(o => new { Name = o.Description, ParamName = o.HasLongName ? $"--{o.LongName}" : $"-{o.ShortName}" })
                    .Select(n =>
                    {
                        var property = action.GetType().GetProperty(n.Name);
                        if (property.PropertyType.IsGenericEnumerable())
                        {
                            var genericCollection = property.GetValue(action) as IEnumerable;
                            var collection = genericCollection.Cast<object>().Select(o => o.ToString());
                            return $"{n.ParamName} {string.Join(" ", collection)}";
                        }
                        else
                        {
                            return $"{n.ParamName} {property.GetValue(action).ToString()}";
                        }
                    })
                    .Aggregate((a, b) => string.Join(" ", a, b));

                var command = $"{context} {subContext} {name} {args}";

                var logFile = Path.GetTempFileName();
                var exeName = Process.GetCurrentProcess().MainModule.FileName;
                command = $"/c \"{exeName}\" {command} >> {logFile}";


                var startInfo = new ProcessStartInfo("cmd")
                {
                    Verb = "runas",
                    Arguments = command,
                    WorkingDirectory = Environment.CurrentDirectory,
                    CreateNoWindow = false,
                    UseShellExecute = true
                };

                var process = Process.Start(startInfo);
                process.WaitForExit();
                errors = File.ReadAllText(logFile);
                return process.ExitCode == ExitCodes.Success;
            }
            else
            {
                throw new ArgumentException($"{nameof(IAction)} type doesn't have {nameof(ActionAttribute)}");
            }
        }

        internal ConsoleApp(string[] args, Assembly assembly, IContainer container)
        {
            _args = args;
            _container = container;
            _actionAttributes = assembly
                .GetTypes()
                .Where(t => typeof(IAction).IsAssignableFrom(t) && !t.IsAbstract)
                .Select(type => type.GetCustomAttributes<ActionAttribute>().Select(a => new TypeAttributePair { Type = type, Attribute = a }))
                .SelectMany(i => i);
        }

        internal IAction Parse()
        {
            if (_args.Length == 0 ||
                (_args.Length == 1 && _helpArgs.Any(ha => _args[0].Replace("-", "").Equals(ha, StringComparison.OrdinalIgnoreCase)))
               )
            {
                return new HelpAction(_actionAttributes, CreateAction);
            }

            var isHelp = _args.First().Equals("help", StringComparison.OrdinalIgnoreCase)
                ? true
                : false;

            var argsStack = new Stack<string>((isHelp ? _args.Skip(1) : _args).Reverse());
            var contextStr = argsStack.Peek();
            var subContextStr = string.Empty;
            var context = Context.None;
            var subContext = Context.None;
            var actionStr = string.Empty;

            if (Enum.TryParse(contextStr, true, out context))
            {
                argsStack.Pop();
                if (argsStack.Any())
                {
                    subContextStr = argsStack.Peek();
                    if (Enum.TryParse(subContextStr, true, out subContext))
                    {
                        argsStack.Pop();
                    }
                }
            }

            if (argsStack.Any())
            {
                actionStr = argsStack.Pop();
            }

            if (string.IsNullOrEmpty(actionStr) || isHelp)
            {
                return new HelpAction(_actionAttributes, CreateAction, contextStr, subContextStr);
            }

            var actionType = _actionAttributes
                .Where(a => a.Attribute.Name.Equals(actionStr, StringComparison.OrdinalIgnoreCase) &&
                            a.Attribute.Context == context && 
                            a.Attribute.SubContext == subContext)
                .SingleOrDefault();

            if (actionType == null)
            {
                return new HelpAction(_actionAttributes, CreateAction, contextStr, subContextStr);
            }

            var action = CreateAction(actionType.Type);
            var args = argsStack.ToArray();
            try
            {
                var parseResult = action.ParseArgs(args);
                if (parseResult.HasErrors)
                {
                    return new HelpAction(_actionAttributes, CreateAction, action, parseResult);
                }
                else
                {
                    return action;
                }
            }
            catch (CliArgumentsException ex)
            {
                ColoredConsole.Error.WriteLine(ex.Message);
                return null;
            }
        }

        internal IAction CreateAction(Type type)
        {
            var ctor = type.GetConstructors()?.SingleOrDefault();
            var args = ctor?.GetParameters()?.Select(p => _container.Resolve(p.ParameterType)).ToArray();
            return args == null || args.Length == 0
                ? (IAction)Activator.CreateInstance(type)
                : (IAction)Activator.CreateInstance(type, args);
        }
    }
}
