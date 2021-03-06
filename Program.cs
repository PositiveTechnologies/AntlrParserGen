﻿using Fclp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace AntlrGen
{
    public enum GenerateStatus
    {
        NotGenerated,
        Generated,
        Error,
    }

    /// <summary>
    /// Generate parser files if grammar has been changed.
    /// Grammar changing date (.g4) stored to the header of generating file (.cs).
    /// If dates are not equal then files will be regenerated.
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            var argsWithUsualSlashes = args.Select(arg => arg.Replace('/', '\\')).ToArray(); // TODO: bug in FluentCommandLineParser.

            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            var cmdParser = new FluentCommandLineParser();

            string lexerFile = null;
            string parserFile = null;
            string lexerSuperClass = null;
            string parserSuperClass = null;
            string packageName = null;
            bool listener = false;
            string output = null;
            bool standard = false;

            cmdParser.Setup<string>("lexer").Callback(lexer => lexerFile = NormDirSeparator(lexer));
            cmdParser.Setup<string>("parser").Callback(parser => parserFile = NormDirSeparator(parser));
            cmdParser.Setup<string>("package").Callback(package => packageName = package);
            cmdParser.Setup<bool>("listener").Callback(l => listener = l);
            cmdParser.Setup<string>("output").Callback(o => output = NormDirSeparator(o));
            cmdParser.Setup<string>("lexerSuperClass").Callback(param => lexerSuperClass = param);
            cmdParser.Setup<string>("parserSuperClass").Callback(param => parserSuperClass = param);
            cmdParser.Setup<bool>("standard").Callback(param => standard = param);

            var result = cmdParser.Parse(argsWithUsualSlashes);
            if (!result.HasErrors)
            {
                GenerateStatus generateStatus = GenerateStatus.NotGenerated;
                if (!string.IsNullOrEmpty(lexerFile))
                {
                    generateStatus = GenerateCode(lexerFile, packageName, true, listener, output, lexerSuperClass, standard);
                }
                if (generateStatus == GenerateStatus.Error)
                {
                    Console.Error.WriteLine("Code generation error");
                }

                if (!string.IsNullOrEmpty(parserFile))
                {
                    generateStatus = GenerateCode(parserFile, packageName, false, listener, output, parserSuperClass, standard);
                }
                if (generateStatus == GenerateStatus.Error)
                {
                    Console.Error.WriteLine("Code generation error");
                }
            }
            else
            {
                var errorMessage = "Command line arguments processing error: " + result.ErrorText;
                Console.Error.WriteLine(errorMessage);
            }
        }

        private static GenerateStatus GenerateCode(string grammarFileName, string packageName, bool lexer, bool listener, string output, string superClass, bool standard)
        {
            if (!Path.IsPathRooted(grammarFileName))
            {
                grammarFileName = Path.Combine(Environment.CurrentDirectory, grammarFileName);
            }

            DateTime grammarModifyDate = File.GetLastWriteTime(grammarFileName);
            grammarModifyDate = DateTime.Parse(grammarModifyDate.ToString());
            grammarModifyDate = new DateTime(
                grammarModifyDate.Year, grammarModifyDate.Month, grammarModifyDate.Day,
                grammarModifyDate.Hour, grammarModifyDate.Minute, grammarModifyDate.Second);
            DateTime maxModifyDate = grammarModifyDate;
            string shortGrammarFileName = Path.GetFileName(grammarFileName);
            string grammarFileDir = Path.GetDirectoryName(grammarFileName);
            string outputDirectory = output ?? Path.Combine(grammarFileDir, "Generated");
            if (!Path.IsPathRooted(outputDirectory))
            {
                outputDirectory = Path.Combine(Environment.CurrentDirectory, outputDirectory);
            }
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
            string generatedFileName = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(grammarFileName));
            if (!lexer && !Path.GetFileNameWithoutExtension(grammarFileName).EndsWith("Parser"))
            {
                generatedFileName += "Parser";
            }
            generatedFileName += ".cs";
            bool generate = false;
            var importedGrammarFileNames = new List<string>() { shortGrammarFileName };
            if (File.Exists(generatedFileName))
            {
                string line1 = "";
                using (StreamReader reader = new StreamReader(generatedFileName))
                {
                    line1 = reader.ReadLine();
                }
                try
                {
                    // Get stored date from comment in generated file.
                    var dateString = line1.Substring(line1.LastIndexOf(' ', line1.LastIndexOf(' ') - 1) + 1);
                    DateTime modifyDateInGenerated = DateTime.Parse(dateString);

                    if (grammarModifyDate > modifyDateInGenerated)
                    {
                        generate = true;
                    }

                    string grammarData = File.ReadAllText(grammarFileName);

                    MatchCollection vocabMatches = Regex.Matches(grammarData, @"tokenVocab\s*=\s*([^;]+);");
                    MatchCollection importMatches = Regex.Matches(grammarData, @"\r\nimport\s+([^;]+);\r\n");

                    // Detect file dependencies: lexer or import files.
                    var imports = new HashSet<string>();
                    foreach (Match match in vocabMatches)
                    {
                        string[] importFileNames = match.Groups[1].Value.Split(',');
                        foreach (string importFileName in importFileNames)
                        {
                            imports.Add(importFileName);
                        }
                    }
                    foreach (Match match in importMatches)
                    {
                        string[] importFileNames = match.Groups[1].Value.Split(',');
                        foreach (var importFileName in importFileNames)
                        {
                            imports.Add(importFileName);
                        }
                    }

                    foreach (string import in imports)
                    {
                        importedGrammarFileNames.Insert(0, import + ".g4");
                        DateTime lastWriteTime = File.GetLastWriteTime(Path.Combine(grammarFileDir, import + ".g4"));
                        lastWriteTime = new DateTime(lastWriteTime.Year, lastWriteTime.Month, lastWriteTime.Day,
                            lastWriteTime.Hour, lastWriteTime.Minute, lastWriteTime.Second);
                        if (lastWriteTime != modifyDateInGenerated)
                        {
                            generate = true;
                            if (lastWriteTime > maxModifyDate)
                            {
                                maxModifyDate = lastWriteTime;
                            }
                        }
                    }
                }
                catch
                {
                    generate = true;
                }
            }
            else
            {
                // If generating file does not exist then generate new file.
                generate = true;
            }

            // Regenerated files if required.
            GenerateStatus result = GenerateStatus.NotGenerated;
            if (generate)
            {
                if (!ProcessUtils.IsProcessCanBeExecuted("java"))
                {
                    Console.WriteLine("java is not installed or java path is not specified.");
                    return GenerateStatus.Error;
                }

                string antlrJarName = standard ? "antlr-4.7.1-standard.jar" : "antlr-4.6.4-optimized.jar";
                string antlrFullJarFileName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), antlrJarName);
                string lexerParser = lexer ? "Lexer" : "Parser";
                Console.WriteLine($"{lexerParser} for {shortGrammarFileName} generation...");
                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "java";
                string visitorListenerStr = (listener ? "-listener " : "-no-listener ") + "-visitor";
                string superClassParam = string.IsNullOrEmpty(superClass) ? "" : $"-DsuperClass={superClass}";
                string packageParam = string.IsNullOrEmpty(packageName) ? "" : $"-package {packageName}";
                process.StartInfo.Arguments = $@"-jar ""{antlrFullJarFileName}"" -o ""{outputDirectory}"" ""{grammarFileName}"" -Dlanguage={(standard ? "CSharp" : "CSharp_v4_5")} {visitorListenerStr} {superClassParam} -Werror {packageParam}";
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.UseShellExecute = false;
                process.Start();
                process.WaitForExit(7500);

                string outputText = process.StandardOutput.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(outputText))
                {
                    Console.Write(outputText);
                }
                string errorText = process.StandardError.ReadToEnd();
                if (string.IsNullOrWhiteSpace(errorText) || errorText.Contains("Picked up _JAVA_OPTIONS")) // TODO: fix workaround
                {
                    string grammarsString = string.Join(", ", importedGrammarFileNames);
                    string modifyDateString = $"// {grammarsString} date: {maxModifyDate}";
                    string generateCode = File.ReadAllText(generatedFileName);
                    string resultCode = modifyDateString + Environment.NewLine + generateCode;
                    File.WriteAllText(generatedFileName, resultCode);
                    result = GenerateStatus.Generated;
                    Console.WriteLine($"{lexerParser} for {shortGrammarFileName} has been generated.");
                }
                else
                {
                    Console.WriteLine($"Arguments: {process.StartInfo.Arguments}");
                    Console.WriteLine($"Error: {errorText}");
                    File.Delete(generatedFileName);
                    result = GenerateStatus.Error;
                    Console.WriteLine($"{lexerParser} for {shortGrammarFileName} generation error.");
                }
            }
            else
            {
                Console.WriteLine($"{shortGrammarFileName} has not been changed. Parser has not been generated.");
            }
            return result;
        }

        private static string NormDirSeparator(string path)
        {
            return path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
