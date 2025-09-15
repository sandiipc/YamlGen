// using System;
// using System.Collections.Generic;
// using System.Diagnostics;
// using System.IO;
// using System.Linq;
// using System.Net.Http;
// using System.Net.Http.Headers;
// using System.Text;
// using System.Text.Json;
// using System.Text.RegularExpressions;
// using System.Threading.Tasks;
// using Microsoft.AspNetCore.Builder;
// using Microsoft.AspNetCore.Hosting;
// using Microsoft.AspNetCore.Http;
// using Microsoft.Extensions.Hosting;
// using Microsoft.Extensions.Logging;

// var builder = WebApplication.CreateBuilder(args);
// builder.Logging.ClearProviders();
// builder.Logging.AddConsole();
// var app = builder.Build();

// // Serve static files if present
// app.UseDefaultFiles();
// app.UseStaticFiles();

// // Redirect root to /index.html always
// app.MapGet("/", (HttpContext ctx) => Results.Redirect("/index.html"));

// app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// // Agentic endpoint - generates YAML using a small "agent" that leverages an LLM (OpenAI Chat API).
// app.MapPost("/api/generate", async (HttpRequest req, ILogger<Program> logger) =>
// {
//     try
//     {
//         var body = await new StreamReader(req.Body).ReadToEndAsync();
//         if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("Empty request body. Provide JSON or plain text prompt.");

//         string repoUrl = null;
//         bool includeSonar = false;
//         bool includeFortify = false;

//         try
//         {
//             var doc = JsonDocument.Parse(body);
//             if (doc.RootElement.TryGetProperty("repoUrl", out var p)) repoUrl = p.GetString();
//             if (doc.RootElement.TryGetProperty("includeSonar", out var s)) includeSonar = s.GetBoolean();
//             if (doc.RootElement.TryGetProperty("includeFortify", out var f)) includeFortify = f.GetBoolean();
//         }
//         catch
//         {
//             var text = body.Replace("\n", " ").Trim();
//             var urlMatch = Regex.Match(text, @"https?://[\w\-./@]+|git@[\w\-.:]+:[\w\-./]+\.git");
//             if (urlMatch.Success) repoUrl = urlMatch.Value;
//             var sonarMatch = Regex.Match(text, "includeSonar\\s*=\\s*(true|false)", RegexOptions.IgnoreCase);
//             if (sonarMatch.Success) includeSonar = string.Equals(sonarMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
//             var fortifyMatch = Regex.Match(text, "includeFortify\\s*=\\s*(true|false)", RegexOptions.IgnoreCase);
//             if (fortifyMatch.Success) includeFortify = string.Equals(fortifyMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
//         }

//         if (string.IsNullOrWhiteSpace(repoUrl)) return Results.BadRequest("Could not determine repository URL from input. Provide repoUrl in JSON or include a URL in text.");

//         logger.LogInformation("Starting agentic generation for {repo}", repoUrl);

//         var generator = new AgenticPipelineGenerator(logger, Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
//         var (yaml, downloadUrl) = await generator.GeneratePipelineForRepositoryAsync(repoUrl, includeSonar, includeFortify);

//         return Results.Ok(new { yaml, downloadUrl, repo = repoUrl, includeSonar, includeFortify });
//     }
//     catch (Exception ex)
//     {
//         return Results.Problem(detail: ex.ToString());
//     }
// });

// EnsureStaticUi();

// app.Run();

// void EnsureStaticUi()
// {
//     var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
//     Directory.CreateDirectory(www);
//     var index = Path.Combine(www, "index.html");
//     if (!File.Exists(index))
//     {
//         File.WriteAllText(index, @"<!doctype html>
// <html>
// <head>
//   <meta charset=""utf-8"" />
//   <title>Agentic Azure DevOps YAML Generator</title>
//   <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
//   <style>
//     body{font-family:Inter,system-ui,Segoe UI,Roboto,Helvetica,Arial;margin:2rem}
//     textarea{width:100%;height:160px;font-family:monospace;font-size:14px}
//     pre{background:#f4f4f4;padding:1rem;border-radius:6px;overflow:auto}
//     .controls{display:flex;gap:8px;margin-top:8px}
//   </style>
// </head>
// <body>
//   <h1>Agentic Azure DevOps YAML Generator</h1>
//   <p>Paste a repository URL or type a prompt like:<br><code>Generate pipeline for https://github.com/org/repo includeSonar=true includeFortify=false</code></p>
//   <textarea id=""prompt"">Generate pipeline for https://github.com/your-org/your-repo includeSonar=true includeFortify=true</textarea>
//   <div class=""controls""><button id=""run"">Generate YAML</button><span id=""status""></span></div>
//   <h3>Generated YAML</h3>
//   <pre id=""output"">(results will appear here)</pre>
//   <a id=""download"" href="""">Download YAML</a>
// <script>
// document.getElementById('run').addEventListener('click', async ()=>{
//   const prompt = document.getElementById('prompt').value;
//   document.getElementById('status').textContent = 'Generating...';
//   try{
//     const res = await fetch('/api/generate',{method:'POST',body:prompt});
//     const data = await res.json();
//     if(res.ok){
//       document.getElementById('output').textContent = data.yaml;
//       document.getElementById('download').href = data.downloadUrl || '#';
//       document.getElementById('download').style.display = data.downloadUrl ? 'inline' : 'none';
//     } else {
//       document.getElementById('output').textContent = JSON.stringify(data,null,2);
//     }
//   }catch(e){document.getElementById('output').textContent = e.toString();}
//   document.getElementById('status').textContent = '';
// });
// </script>
// </body>
// </html>");
//     }
// }

// public class AgenticPipelineGenerator
// {
//     private readonly ILogger _logger;
//     private readonly string _openAiKey;
//     private readonly HttpClient _http;

//     public AgenticPipelineGenerator(ILogger logger, string openAiKey)
//     {
//         _logger = logger;
//         _openAiKey = openAiKey;
//         _http = new HttpClient();
//         if (!string.IsNullOrWhiteSpace(_openAiKey))
//         {
//             _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _openAiKey);
//         }
//     }

//     public async Task<(string yaml, string downloadUrl)> GeneratePipelineForRepositoryAsync(string repoUrl, bool includeSonar, bool includeFortify)
//     {
//         // 1) Clone the repo to a temp folder (shallow)
//         var temp = Path.Combine(Path.GetTempPath(), "repo_" + Guid.NewGuid().ToString("N"));
//         Directory.CreateDirectory(temp);
//         _logger.LogInformation("Cloning into {dir}", temp);
//         var cloneSuccess = await RunGitCloneAsync(repoUrl, temp);
//         if (!cloneSuccess) throw new Exception("Failed to clone repository. Ensure git is installed and URL is accessible.");

//         try
//         {
//             // 2) Detect languages
//             var languages = DetectLanguages(temp);
//             _logger.LogInformation("Detected languages: {langs}", string.Join(",", languages));

//             // 3) Baseline YAML (template)
//             var baselineYaml = BuildAzurePipelineYaml(languages, includeSonar, includeFortify);

//             // 4) Use LLM to refine and adapt baseline YAML to repo specifics (agent step)
//             var refinedYaml = await GenerateWithLLMAsync(repoUrl, temp, languages, baselineYaml, includeSonar, includeFortify);

//             // 5) Validate (lightweight)
//             if (!BasicYamlLooksValid(refinedYaml))
//             {
//                 _logger.LogWarning("Refined YAML failed basic validation. Requesting one retry from LLM.");
//                 // Ask LLM to fix
//                 refinedYaml = await FixWithLLMAsync(refinedYaml, "Basic validation failed. Ensure YAML starts with 'trigger:' and uses valid indentation.");
//             }

//             // 6) Save file for download
//             var www = Path.Combine(AppContext.BaseDirectory, "wwwroot", "generated");
//             Directory.CreateDirectory(www);
//             var id = Guid.NewGuid().ToString("N");
//             var path = Path.Combine(www, $"pipeline_{id}.yaml");
//             await File.WriteAllTextAsync(path, refinedYaml);
//             // Return a relative URL to the file (served by static files)
//             var downloadUrl = $"/generated/pipeline_{id}.yaml";

//             return (refinedYaml, downloadUrl);
//         }
//         finally
//         {
//             // cleanup - try best-effort
//             try { Directory.Delete(temp, true); } catch { }
//         }
//     }

//     private async Task<bool> RunGitCloneAsync(string repoUrl, string destination)
//     {
//         try
//         {
//             var psi = new ProcessStartInfo("git", $"clone --depth 1 \"{repoUrl}\" \"{destination}\"")
//             {
//                 RedirectStandardOutput = true,
//                 RedirectStandardError = true,
//                 UseShellExecute = false,
//                 CreateNoWindow = true,
//             };
//             var p = Process.Start(psi);
//             var stdout = await p.StandardOutput.ReadToEndAsync();
//             var stderr = await p.StandardError.ReadToEndAsync();
//             await p.WaitForExitAsync();
//             _logger.LogInformation(stdout);
//             if (p.ExitCode != 0)
//             {
//                 _logger.LogWarning("git clone returned {code} stderr: {err}", p.ExitCode, stderr);
//                 return false;
//             }
//             return true;
//         }
//         catch (Exception ex)
//         {
//             _logger.LogError(ex, "Exception while running git clone");
//             return false;
//         }
//     }

//     private List<string> DetectLanguages(string repoPath)
//     {
//         var extToLang = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
//         {
//             {".cs","dotnet"},
//             {".fs","dotnet"},
//             {".vb","dotnet"},
//             {".py","python"},
//             {".js","node"},
//             {".ts","node"},
//             {".java","java"},
//             {"pom.xml","java"},
//             {"build.gradle","java"},
//             {"package.json","node"},
//             {"requirements.txt","python"},
//             {"setup.py","python"}
//         };

//         var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
//         foreach (var file in Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories))
//         {
//             var name = Path.GetFileName(file);
//             var ext = Path.GetExtension(file);
//             if (extToLang.TryGetValue(name, out var langByName)) { found.Add(langByName); continue; }
//             if (extToLang.TryGetValue(ext, out var lang)) { found.Add(lang); }
//         }

//         if (found.Count == 0) found.Add("dotnet"); // default to dotnet
//         return found.ToList();
//     }

//     private bool BasicYamlLooksValid(string yaml)
//     {
//         if (string.IsNullOrWhiteSpace(yaml)) return false;
//         // very lightweight checks
//         if (!yaml.TrimStart().StartsWith("trigger:")) return false;
//         if (!yaml.Contains("stages:") && !yaml.Contains("- stage:")) return false;
//         return true;
//     }

//     private async Task<string> GenerateWithLLMAsync(string repoUrl, string repoPath, List<string> languages, string baselineYaml, bool includeSonar, bool includeFortify)
//     {
//         // Build a concise system + user prompt
//         var system = @"You are an expert Azure DevOps engineer and prompt engineering assistant.
// You will improve and tailor the given Azure DevOps pipeline YAML to the repository provided.
// Return only valid YAML content as the final answer (no exposition).";

//         var userSb = new StringBuilder();
//         userSb.AppendLine($"Repository URL: {repoUrl}");
//         userSb.AppendLine($"Detected languages: {string.Join(", ", languages)}");
//         userSb.AppendLine($"Include Sonar: {includeSonar}");
//         userSb.AppendLine($"Include Fortify: {includeFortify}");
//         userSb.AppendLine();
//         userSb.AppendLine("Repository file sample (list up to 20 files found at repo root):");

//         // include a few files to help the LLM
//         var rootFiles = Directory.EnumerateFiles(repoPath, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).Take(20);
//         foreach (var f in rootFiles) userSb.AppendLine($"- {f}");
//         userSb.AppendLine();
//         userSb.AppendLine("Baseline YAML (improve and adapt to repo):");
//         userSb.AppendLine("```yaml");
//         userSb.AppendLine(baselineYaml);
//         userSb.AppendLine("```");
//         userSb.AppendLine();
//         userSb.AppendLine("Instructions:");
//         userSb.AppendLine("- Adjust pool, tasks, and steps to match detected languages.");
//         userSb.AppendLine("- Ensure the YAML is syntactically valid for Azure DevOps.");
//         userSb.AppendLine("- Keep sonar/fortify steps if requested but suggest placeholders for secrets/service connections.");
//         userSb.AppendLine("- Output ONLY the YAML content (no markdown fences).");

//         var user = userSb.ToString();

//         var llmResponse = await CallOpenAiChatAsync(system, user, maxTokens: 1200, model: "gpt-3.5-turbo");
//         // LLM responses sometimes include markdown fences - strip them
//         var cleaned = StripMarkdownFences(llmResponse);
//         return cleaned;
//     }

//     private async Task<string> FixWithLLMAsync(string currentYaml, string reason)
//     {
//         var system = @"You are an assistant that fixes Azure DevOps YAML. Return valid YAML only.";
//         var user = $"The current YAML failed validation for this reason: {reason}\nPlease fix the YAML below and return only the corrected YAML:\n\n{currentYaml}";
//         var llmResponse = await CallOpenAiChatAsync(system, user, maxTokens: 800, model: "gpt-3.5-turbo");
//         var cleaned = StripMarkdownFences(llmResponse);
//         return cleaned;
//     }

//     private string StripMarkdownFences(string text)
//     {
//         if (string.IsNullOrEmpty(text)) return text;
//         // Remove ```yaml or ``` fences and leading/trailing whitespace
//         var t = Regex.Replace(text, @"^```(?:yaml)?\r?\n", "", RegexOptions.IgnoreCase);
//         t = Regex.Replace(t, @"\r?\n```$", "", RegexOptions.IgnoreCase);
//         return t.Trim();
//     }

//     private async Task<string> CallOpenAiChatAsync(string systemPrompt, string userPrompt, int maxTokens = 800, string model = "gpt-3.5-turbo")
//     {
//         if (string.IsNullOrWhiteSpace(_openAiKey))
//         {
//             _logger.LogWarning("OPENAI_API_KEY not configured - returning baseline by default.");
//             return userPrompt; // fallback (not ideal) - returns baseline included in prompt
//         }

//         var endpoint = "https://api.openai.com/v1/chat/completions";
//         var payload = new
//         {
//             model = model,
//             messages = new[]
//             {
//                 new { role = "system", content = systemPrompt },
//                 new { role = "user", content = userPrompt }
//             },
//             max_tokens = maxTokens,
//             temperature = 0.0
//         };

//         var json = JsonSerializer.Serialize(payload);
//         var content = new StringContent(json, Encoding.UTF8, "application/json");
//         var res = await _http.PostAsync(endpoint, content);
//         var respText = await res.Content.ReadAsStringAsync();
//         if (!res.IsSuccessStatusCode)
//         {
//             _logger.LogError("OpenAI API returned {code}: {body}", res.StatusCode, respText);
//             throw new Exception($"OpenAI API error: {res.StatusCode} - {respText}");
//         }

//         using var doc = JsonDocument.Parse(respText);
//         var root = doc.RootElement;
//         var choice = root.GetProperty("choices")[0];
//         var message = choice.GetProperty("message").GetProperty("content").GetString();
//         return message ?? "";
//     }

//     // --- The original YAML template generator methods (kept as baseline) ---
//     private string BuildAzurePipelineYaml(List<string> languages, bool includeSonar, bool includeFortify)
//     {
//         var sb = new System.Text.StringBuilder();
//         sb.AppendLine("trigger:");
//         sb.AppendLine("  branches:");
//         sb.AppendLine("    include:");
//         sb.AppendLine("      - main");
//         sb.AppendLine("      - master");
//         sb.AppendLine();
//         sb.AppendLine("pr:");
//         sb.AppendLine("  branches:");
//         sb.AppendLine("    include:");
//         sb.AppendLine("      - '*'");
//         sb.AppendLine();
//         sb.AppendLine("stages:");

//         foreach (var lang in languages)
//         {
//             switch (lang.ToLowerInvariant())
//             {
//                 case "dotnet":
//                     sb.AppendLine(GenerateDotNetStage(includeSonar, includeFortify));
//                     break;
//                 case "python":
//                     sb.AppendLine(GeneratePythonStage(includeSonar, includeFortify));
//                     break;
//                 case "node":
//                     sb.AppendLine(GenerateNodeStage(includeSonar, includeFortify));
//                     break;
//                 case "java":
//                     sb.AppendLine(GenerateJavaStage(includeSonar, includeFortify));
//                     break;
//                 default:
//                     sb.AppendLine($"# Unknown language: {lang} - no stage generated\n");
//                     break;
//             }
//         }

//         sb.AppendLine(@"- stage: Deploy
//   displayName: 'Deploy (placeholder)'
//   dependsOn: []
//   jobs:
//   - deployment: DeployJob
//     environment: 'dev'
//     strategy:
//       runOnce:
//         deploy:
//           steps:
//           - script: echo 'Add deployment tasks (ARM/Bicep/Helm/Kubernetes) here'");

//         return sb.ToString();
//     }

//     private string GenerateDotNetStage(bool sonar, bool fortify)
//     {
//         var sb = new System.Text.StringBuilder();
//         sb.AppendLine("- stage: Build_DotNet");
//         sb.AppendLine("  displayName: 'Build & Test (.NET)'");
//         sb.AppendLine("  jobs:");
//         sb.AppendLine("  - job: Build");
//         sb.AppendLine("    pool:\n      vmImage: 'windows-latest'\n    steps:");
//         sb.AppendLine("    - task: UseDotNet@2\n      inputs:\n        packageType: 'sdk'\n        version: '8.x'\n        includePreview: true");
//         sb.AppendLine("    - script: dotnet restore\n      displayName: 'dotnet restore'");
//         sb.AppendLine("    - script: dotnet build --configuration Release --no-restore\n      displayName: 'dotnet build'");
//         sb.AppendLine("    - script: dotnet test --configuration Release --no-build --verbosity normal\n      displayName: 'dotnet test'");

//         if (sonar)
//         {
//             sb.AppendLine();
//             sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud' # service connection name\n        organization: 'your-org'\n        scannerMode: 'MSBuild'\n        projectKey: 'your-project-key'\n        projectName: 'your-project-name'");
//             sb.AppendLine("    - script: dotnet build\n      displayName: 'dotnet build (for Sonar)'\n");
//             sb.AppendLine("    - task: SonarCloudAnalyze@1");
//             sb.AppendLine("    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
//         }

//         if (fortify)
//         {
//             sb.AppendLine();
//             sb.AppendLine("    - task: PowerShell@2\n      displayName: 'Run Fortify SAST Scan (example)'");
//             sb.AppendLine("      inputs:\n        targetType: 'inline'\n        script: |\n          Write-Host 'This is a placeholder for Fortify scan steps.\nYou should invoke Fortify SCA (sourceanalyzer) here, upload results to SSC, and fail/mark build accordingly.'");
//         }

//         sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

//         return sb.ToString();
//     }

//     private string GenerateNodeStage(bool sonar, bool fortify)
//     {
//         var sb = new System.Text.StringBuilder();
//         sb.AppendLine("- stage: Build_Node");
//         sb.AppendLine("  displayName: 'Build & Test (Node.js)'");
//         sb.AppendLine("  jobs:");
//         sb.AppendLine("  - job: Build");
//         sb.AppendLine("    pool:\n      vmImage: 'ubuntu-latest'\n    steps:");
//         sb.AppendLine("    - task: NodeTool@0\n      inputs:\n        versionSpec: '18.x'\n        checkLatest: true");
//         sb.AppendLine("    - script: npm install\n      displayName: 'npm install'");
//         sb.AppendLine("    - script: npm test || true\n      displayName: 'npm test (continue on failures)'");

//         if (sonar)
//         {
//             sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud'\n        organization: 'your-org'\n        scannerMode: 'CLI'\n        configMode: 'manual'\n        cliProjectKey: 'your-project-key'\n        cliProjectName: 'your-project-name'");
//             sb.AppendLine("    - script: npm run build || true\n      displayName: 'npm build (for Sonar)'");
//             sb.AppendLine("    - task: SonarCloudAnalyze@1\n    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
//         }

//         if (fortify)
//         {
//             sb.AppendLine("    - script: echo 'Run Fortify SCA for Node (placeholder)'");
//         }

//         sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

//         return sb.ToString();
//     }

//     private string GeneratePythonStage(bool sonar, bool fortify)
//     {
//         var sb = new System.Text.StringBuilder();
//         sb.AppendLine("- stage: Build_Python");
//         sb.AppendLine("  displayName: 'Build & Test (Python)'");
//         sb.AppendLine("  jobs:");
//         sb.AppendLine("  - job: Build");
//         sb.AppendLine("    pool:\n      vmImage: 'ubuntu-latest'\n    steps:");
//         sb.AppendLine("    - task: UsePythonVersion@0\n      inputs:\n        versionSpec: '3.10'\n        addToPath: true");
//         sb.AppendLine("    - script: python -m pip install -r requirements.txt || true\n      displayName: 'pip install'");
//         sb.AppendLine("    - script: pytest -q || true\n      displayName: 'pytest'");

//         if (sonar)
//         {
//             sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud'\n        organization: 'your-org'\n        scannerMode: 'CLI'\n        configMode: 'manual'\n        cliProjectKey: 'your-project-key'\n        cliProjectName: 'your-project-name'");
//             sb.AppendLine("    - script: sonar-scanner\n      displayName: 'Run SonarScanner'\n");
//             sb.AppendLine("    - task: SonarCloudAnalyze@1\n    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
//         }

//         if (fortify)
//         {
//             sb.AppendLine("    - script: echo 'Run Fortify SCA for Python (placeholder)'");
//         }

//         sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

//         return sb.ToString();
//     }

//     private string GenerateJavaStage(bool sonar, bool fortify)
//     {
//         var sb = new System.Text.StringBuilder();
//         sb.AppendLine("- stage: Build_Java");
//         sb.AppendLine("  displayName: 'Build & Test (Java)'");
//         sb.AppendLine("  jobs:");
//         sb.AppendLine("  - job: Build");
//         sb.AppendLine("    pool:\n      vmImage: 'ubuntu-latest'\n    steps:");
//         sb.AppendLine("    - task: MavenAuthenticate@0\n      inputs:\n        artifactsFeeds: ''");
//         sb.AppendLine("    - script: mvn -B -DskipTests=false package\n      displayName: 'mvn package'");

//         if (sonar)
//         {
//             sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud'\n        organization: 'your-org'\n        scannerMode: 'CLI'\n        configMode: 'manual'\n        cliProjectKey: 'your-project-key'\n        cliProjectName: 'your-project-name'");
//             sb.AppendLine("    - script: mvn sonar:sonar\n      displayName: 'mvn sonar:sonar'");
//             sb.AppendLine("    - task: SonarCloudAnalyze@1\n    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
//         }

//         if (fortify)
//         {
//             sb.AppendLine("    - script: echo 'Run Fortify SCA for Java (placeholder)'");
//         }

//         sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

//         return sb.ToString();
//     }
// }
