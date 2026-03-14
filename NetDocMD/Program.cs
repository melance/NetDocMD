using System.CommandLine;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetDocMD;

internal static partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        var xmlOption = new Option<FileInfo>("--xml", "-x", "-doc")
        {
            Description = "Path to the XML documentation file.",
            Required = true
        };

        var assemblyOption = new Option<FileInfo>("--assembly", "-a")
        {
            Description = "Optional path to the Assembly to document. Defaults to the XML file path with a .dll extension."
        };

        var outputOption = new Option<FileInfo>("--output")
        {
            Description = "Path to write the generated markdown file."
        };

        var readmeOption = new Option<FileInfo?>("--readme")
        {
            Description = "Optional README.md file to update in-place using markers."
        };

        var startMarkerOption = new Option<string>("--start-marker")
        {
            Description = "Marker indicating the start of the generated section.",
            DefaultValueFactory = _ => "<!-- API:START -->"
        };

        var endMarkerOption = new Option<string>("--end-marker")
        {
            Description = "Marker indicating the end of the generated section.",
            DefaultValueFactory = _ => "<!-- API:END -->"
        };

        var shortOption = new Option<Boolean>("--short", "--s")
        {
            Description = "Only output namespaces and classes.",
            DefaultValueFactory = _ => false
        };

        var rootCommand = new RootCommand("Generates markdown from C# XML documentation comments.")
        {
            xmlOption,
            outputOption,
            readmeOption,
            startMarkerOption,
            endMarkerOption,
            shortOption
        };

        rootCommand.SetAction(parseResult =>
        {
            FileInfo xmlFile = parseResult.GetValue(xmlOption)!;
            FileInfo assemblyFile = ResolveAssembly(xmlFile, parseResult.GetValue(assemblyOption)); 
            FileInfo outputFile = parseResult.GetValue(outputOption)!;
            FileInfo? readmeFile = parseResult.GetValue(readmeOption);
            String startMarker = parseResult.GetValue(startMarkerOption)!;
            String endMarker = parseResult.GetValue(endMarkerOption)!;
            Boolean s = parseResult.GetValue(shortOption);
            
            try
            {
                if (outputFile is null)
                {
                    String xmlDir = xmlFile.DirectoryName ?? ".";
                    outputFile = new FileInfo(Path.Combine(xmlDir, "README.generated.md"));
                }

                Execute(xmlFile, assemblyFile, outputFile, readmeFile, startMarker, endMarker, s);
                parseResult.InvocationConfiguration.Output.WriteLine($"Generated markdown: {outputFile.FullName}");

                if (readmeFile is not null)
                    parseResult.InvocationConfiguration.Output.WriteLine($"Updated README: {readmeFile.FullName}");

                return 0;
            }
            catch (Exception ex)
            {
                parseResult.InvocationConfiguration.Error.WriteLine(ex.Message);
                return 1;
            }
        });

        return rootCommand.Parse(args).Invoke();
    }

    #region Execute
    private static void Execute(FileInfo xmlFile,
                                FileInfo assemblyFile,
                                FileInfo outputFile,
                                FileInfo? readmeFile,
                                string startMarker,
                                string endMarker,
                                Boolean s)
    {
        if (!xmlFile.Exists)
            throw new FileNotFoundException($"XML documentation file not found: {xmlFile.FullName}");
        if (!assemblyFile.Exists)
            throw new FileNotFoundException($"Assembly file not found: {assemblyFile.FullName}");

        var markdown = GenerateMarkdown(xmlFile.FullName, assemblyFile.FullName, s);

        Directory.CreateDirectory(outputFile.DirectoryName ?? ".");
        File.WriteAllText(outputFile.FullName, markdown, Encoding.UTF8);

        if (readmeFile is not null)
        {
            UpdateReadme(readmeFile, markdown, startMarker, endMarker);
        }
    }
    #endregion

    private static String GenerateMarkdown(String xmlPath, String assemblyPath, Boolean s)
    {
        XDocument doc = XDocument.Load(xmlPath);
        Assembly assembly = Assembly.LoadFrom(assemblyPath);
        Dictionary<String, XElement> members = doc.Root?
                                                  .Element("members")?
                                                  .Elements("member")
                                                  .Select(x => new
                                                  {
                                                      Name = (String?)x.Attribute("name"),
                                                      Element = x
                                                  })
                                                  .Where(x => !String.IsNullOrWhiteSpace(x.Name))
                                                  .ToDictionary(x => x.Name!, x => x.Element, StringComparer.Ordinal)
                                                  ?? new Dictionary<string, XElement>(StringComparer.Ordinal);
        var types = assembly.GetExportedTypes()
                            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
                            .ThenBy(t => t.Name, StringComparer.Ordinal)
                            .GroupBy(t => t.Namespace ?? String.Empty, StringComparer.Ordinal);

        StringBuilder sb = new();

        foreach (var namespaceGroup in types)
        {
            if (!String.IsNullOrWhiteSpace(namespaceGroup.Key))
            {
                // Write the Namespace
                sb.AppendLine($"# {namespaceGroup.Key}");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();

                foreach (Type type in namespaceGroup)
                {
                    String typeDisplayName = GetTypeDisplayName(type);
                    String typeKind = GetTypeKind(type);
                    String typeMemberId = GetTypeMemberId(type);

                    members.TryGetValue(typeMemberId, out XElement? typeDoc);

                    String typeSummary = Clean(typeDoc?.Element("summary")?.Value);

                    // Write the Class, Enum, Interface, or Struct name
                    sb.AppendLine($"## {typeDisplayName} <small> &bull; {typeKind}</small>");

                    sb.AppendLine();

                    if (!String.IsNullOrWhiteSpace(typeSummary))
                    {
                        sb.AppendLine(typeSummary);
                        sb.AppendLine();
                    }

                    if (!s)
                    {
                        // Write constructor definitions
                        List<ConstructorInfo> constructors = [.. type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                                                 .OrderBy(c => c.GetParameters().Length)];

                        foreach (var constructor in constructors)
                        {
                            String constructorMemberId = GetConstructorMemberId(constructor);
                            members.TryGetValue(constructorMemberId, out XElement? constructorDoc);
                            sb.Append(GenerateConstructorInfo(constructor, constructorDoc));
                        }

                        // Write method definitions
                        List<MethodInfo> methods = [.. type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                                       .Where(m => !m.IsSpecialName)
                                                       .OrderBy(m => m.Name, StringComparer.Ordinal)
                                                       .ThenBy(m => m.GetParameters().Length)];
                        foreach (var method in methods)
                        {
                            String methodMemberId = GetMethodMemberId(method);
                            members.TryGetValue(methodMemberId, out XElement? methodDoc);

                            sb.Append(GenerateMethodInfo(method, methodDoc));
                        }
                    }
                }
            }
        }



        return sb.ToString();
    }

    private static String GenerateConstructorInfo(ConstructorInfo constructor, XElement? constructorDoc)
    {
        StringBuilder sb = new StringBuilder();
        String constructorDisplayName = GetConstructorDisplayName(constructor);
        String summary = Clean(constructorDoc?.Element("summary")?.Value);

        sb.AppendLine($"### {constructorDisplayName}");
        sb.AppendLine();

        if (!String.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine(summary);
            sb.AppendLine();
        }

        List<ParameterInfo> parameters = [.. constructor.GetParameters()];
        List<XElement> parameterDocs = constructorDoc?.Elements("param").ToList() ?? [];
        sb.Append(GenerateParameterInfo(parameters, parameterDocs));        

        return sb.ToString();
    }

    private static String GenerateMethodInfo(MethodInfo method, XElement? methodDoc)
    {
        var sb = new StringBuilder();
        String methodDisplayName = GetMethodDisplayName(method);
        String summary = Clean(methodDoc?.Element("summary")?.Value);
        String returns = Clean(methodDoc?.Element("returns")?.Value);
        String remarks = Clean(methodDoc?.Element("remarks")?.Value);

        sb.Append($"### {methodDisplayName}");
        sb.AppendLine();

        if (!String.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine(summary);
            sb.AppendLine();
        }

        List<XElement> typeParameters = methodDoc?.Elements("typeparam")?.ToList() ?? [];
        sb.Append(GenerateTypeParameterInfo(typeParameters));
        
        List<ParameterInfo> reflectedParameters = [.. method.GetParameters()];
        List<XElement> parameterDocs = methodDoc?.Elements("param").ToList() ?? [];        
        sb.Append(GenerateParameterInfo(reflectedParameters, parameterDocs));
        
        if (method.ReturnType != typeof(void))
        {
            sb.AppendLine("#### Returns");
            sb.AppendLine();
            sb.AppendLine($"`{GetTypeDisplayName(method.ReturnType)}`");

            if (!String.IsNullOrWhiteSpace(returns))
            {
                sb.AppendLine();
                sb.AppendLine(returns);
            }

            sb.AppendLine();
        }

        if (!String.IsNullOrWhiteSpace(remarks))
        {
            sb.AppendLine("#### Remarks");
            sb.AppendLine();
            sb.AppendLine(remarks);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static String GenerateParameterInfo(List<ParameterInfo> parameters, List<XElement> parameterDocs)
    {
        var sb = new StringBuilder();

        if (parameters.Count > 0)
        {
            sb.AppendLine("#### Parameters");
            sb.AppendLine();
            sb.AppendLine("| Name | Type | Description |");
            sb.AppendLine("|------|------|-------------|");

            foreach (var parameter in parameters)
            {
                var parameterDoc = parameterDocs.FirstOrDefault(x => String.Equals((String?)x.Attribute("name"), parameter.Name, StringComparison.Ordinal));
                String parameterDescription = Clean(parameterDoc?.Value);
                sb.AppendLine($"| `{EscapePipe(parameter.Name ?? String.Empty)}` | `{EscapePipe(GetTypeDisplayName(parameter.ParameterType))}` | {EscapePipe(parameterDescription)} |");
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static String GenerateTypeParameterInfo(List<XElement> typeParameters)
    {
        var sb = new StringBuilder();
        if (typeParameters.Count > 0)
        {
            sb.AppendLine("#### Type Parameters");
            sb.AppendLine();
            sb.AppendLine("| Name | Description |");
            sb.AppendLine("|------|-------------|");

            foreach (var item in typeParameters)
            {
                String name = (String?)item.Attribute("name") ?? String.Empty;
                String description = Clean(item.Value);
                sb.AppendLine($"| `{EscapePipe(name)}` | {EscapePipe(description)} |");
            }

            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void UpdateReadme(FileInfo readmeFile,
                                     String generatedMarkdown,
                                     String startMarker,
                                     String endMarker)
    {
        if (!readmeFile.Exists)
            throw new FileNotFoundException($"README file not found: {readmeFile.FullName}");

        var readme = File.ReadAllText(readmeFile.FullName);

        if (!readme.Contains(startMarker, StringComparison.Ordinal) ||
            !readme.Contains(endMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"README does not contain both markers: '{startMarker}' and '{endMarker}'.");
        }

        var replacement = new StringBuilder()
            .AppendLine(startMarker)
            .AppendLine()
            .Append(generatedMarkdown.TrimEnd())
            .AppendLine()
            .Append(endMarker)
            .ToString();

        var pattern = $"{Regex.Escape(startMarker)}.*?{Regex.Escape(endMarker)}";
        var updated = Regex.Replace(readme, pattern, replacement, RegexOptions.Singleline);

        File.WriteAllText(readmeFile.FullName, updated, Encoding.UTF8);
    }

   

    #region Helper Methods
    private static String Clean(String? value)
    {
        if (String.IsNullOrWhiteSpace(value)) return String.Empty;

        return CleanRegex().Replace(value.Trim(), " ");
    }

    private static String EscapePipe(String value) => value.Replace("|", "\\|");

    private static String GetConstructorDisplayName(ConstructorInfo cunstructor)
    {
        String name = cunstructor.DeclaringType?.Name ?? ".ctor";
        String parameters = String.Join(", ", cunstructor.GetParameters().Select(p => $"{GetTypeDisplayName(NormalizeParameterType(p))} {p.Name}"));
        name = RemoveTicks(name);
        return $"{name}({parameters})";
    }

    private static String GetMethodDisplayName(MethodInfo method)
    {
        String name = method.Name;
        String parameters = String.Join(", ", method.GetParameters().Select(p => $"{GetTypeDisplayName(NormalizeParameterType(p))} {p.Name}"));

        if (method.IsGenericMethod)
        {
            String genericArgs = String.Join(", ", method.GetGenericArguments().Select(x => x.Name));
            name += $"<{genericArgs}>";
        }

        return $"{name}({parameters})";
    }

    

    private static String GetTypeDisplayName(Type type)
    {
        if (type.IsByRef)
            return GetTypeDisplayName(type.GetElementType()!);
        if (type.IsArray)
            return $"{GetTypeDisplayName(type.GetElementType()!)}[]";
        if (type.IsGenericParameter)
            return type.Name;
        if (Nullable.GetUnderlyingType(type) is Type underlyingTypeNullable)
            return $"Nullable<{GetTypeDisplayName(underlyingTypeNullable)}>";
        if (type.IsGenericType)
        {
            String name = RemoveTicks(type.Name);
            var genericArgs = String.Join(", ", type.GetGenericArguments().Select(GetTypeDisplayName));
        }
        return RemoveTicks(type.Name);
    }

    private static String GetTypeKind(Type type)
    {
        if (type.IsEnum) return "enum";
        if (type.IsInterface) return "interface";
        if (typeof(MulticastDelegate).IsAssignableFrom(type.BaseType)) return "delegate";
        if (type.IsValueType && !type.IsPrimitive) return "struct";
        return "class";
    }

    private static Type NormalizeParameterType(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;

        if (type.IsByRef)
            type = type.GetElementType()!;

        return type;
    }

    private static String RemoveTicks(String name)
    {
        Int32 tickIndex = name.IndexOf('`');
        if (tickIndex >= 0)
            return name[..tickIndex];
        return name;
    }

    private static FileInfo ResolveAssembly(FileInfo xmlFile, FileInfo? assemblyFile)
    {
        if (assemblyFile is not null) return assemblyFile;

        String dllPath = Path.ChangeExtension(xmlFile.FullName, ".dll");
        if (File.Exists(dllPath))
            return new FileInfo(dllPath);

        String exePath = Path.ChangeExtension(xmlFile.FullName, ".exe");
        if (File.Exists(exePath))
            return new FileInfo(exePath);

        throw new FileNotFoundException($"Could not locate assembly for '{xmlFile.FullName}'. Expected '{dllPath}' or '{exePath}'.");
    }

    #region Assembly to XML Helpers
    private static String GetConstructorMemberId(ConstructorInfo constructor)
    {
        String typeName = GetXmlTypeName(constructor.DeclaringType!);
        String methodName = constructor.IsStatic ? "#cctor" : "#ctor";

        var parameters = constructor.GetParameters();
        if (parameters.Length == 0)
            return $"M:{typeName}.{methodName}";

        String parameterList = String.Join(",", parameters.Select(p => GetXmlParameterTypeName(p.ParameterType)));
        return $"M:{typeName}.{methodName}({parameterList})";
    }

    private static String GetMethodMemberId(MethodInfo method)
    {
        String typeName = GetXmlTypeName(method.DeclaringType!);
        String methodName = method.Name;

        if (method.IsGenericMethodDefinition || method.IsGenericMethod)
            methodName += $"``{method.GetGenericArguments().Length}";

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length == 0)
            return $"M:{typeName}.{methodName}";

        String parameterList = String.Join(", ", parameters.Select(p => GetXmlParameterTypeName(p.ParameterType)));
        return $"M:{typeName}.{methodName}({parameterList})";
    }

    private static String GetTypeMemberId(Type type) => $"T:{GetXmlTypeName(type)}";

    private static String GetXmlTypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            if (type.DeclaringMethod is not null)
                return $"``{type.GenericParameterPosition}";

            return $"`{type.GenericParameterPosition}";
        }

        if (type.IsByRef)
            return $"{GetXmlTypeName(type.GetElementType()!)}[]";

        if (type.IsGenericType)
        {
            if (type.IsGenericTypeDefinition)
            {
                String genericDefinitionName = type.FullName ?? type.Name;
                return genericDefinitionName.Replace('+', '.');
            }
            else
            {
                Type genericDefinition = type.GetGenericTypeDefinition();
                String genericDefinitionName = genericDefinition.FullName ?? genericDefinition.Name;
                genericDefinitionName = RemoveTicks(genericDefinitionName);
                String arguments = String.Join(",", type.GetGenericArguments().Select(GetXmlTypeName));
                return $"{genericDefinitionName.Replace('+', '.')}{{{arguments}}}";
            }
        }

        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static String GetXmlParameterTypeName(Type type) => GetXmlTypeName(type); 
    #endregion

    #endregion

    #region Types
    private sealed record DocItem(string Name, string Description); 
    #endregion

    #region Regex
    [GeneratedRegex(@"\s+")]
    private static partial Regex CleanRegex();
    #endregion
}