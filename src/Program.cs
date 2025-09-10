using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var app = builder.Build();

// Serve static files if present
app.UseDefaultFiles();
app.UseStaticFiles();

// // Always provide a root landing page fallback
// app.MapGet("/", () => Results.Content(
//     "<h1>Azure DevOps YAML Generator Bot</h1><p>Use the <code>/index.html</code> UI or POST to <code>/api/generate</code></p>",
//     "text/html"
// ));

// Redirect root to /index.html always
app.MapGet("/", (HttpContext ctx) => Results.Redirect("/index.html"));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/generate", async (HttpRequest req, ILogger<Program> logger) =>
{
    try
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("Empty request body. Provide JSON or plain text prompt.");

        string repoUrl = null;
        bool includeSonar = false;
        bool includeFortify = false;

        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("repoUrl", out var p)) repoUrl = p.GetString();
            if (doc.RootElement.TryGetProperty("includeSonar", out var s)) includeSonar = s.GetBoolean();
            if (doc.RootElement.TryGetProperty("includeFortify", out var f)) includeFortify = f.GetBoolean();
        }
        catch
        {
            var text = body.Replace("\n", " ").Trim();
            var urlMatch = Regex.Match(text, @"https?://[\w\-./@]+|git@[\w\-.:]+:[\w\-./]+\.git");
            if (urlMatch.Success) repoUrl = urlMatch.Value;
            var sonarMatch = Regex.Match(text, "includeSonar\\s*=\\s*(true|false)", RegexOptions.IgnoreCase);
            if (sonarMatch.Success) includeSonar = string.Equals(sonarMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            var fortifyMatch = Regex.Match(text, "includeFortify\\s*=\\s*(true|false)", RegexOptions.IgnoreCase);
            if (fortifyMatch.Success) includeFortify = string.Equals(fortifyMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(repoUrl)) return Results.BadRequest("Could not determine repository URL from input. Provide repoUrl in JSON or include a URL in text.");

        logger.LogInformation("Starting generation for {repo}", repoUrl);

        var generator = new PipelineGenerator(logger);
        var result = await generator.GeneratePipelineForRepositoryAsync(repoUrl, includeSonar, includeFortify);

        return Results.Ok(new { yaml = result, repo = repoUrl, includeSonar, includeFortify });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.ToString());
    }
});

EnsureStaticUi();

app.Run();

void EnsureStaticUi()
{
    var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    Directory.CreateDirectory(www);
    var index = Path.Combine(www, "index.html");
    if (!File.Exists(index))
    {
        File.WriteAllText(index, @"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"" />
  <title>Azure DevOps YAML Generator Bot</title>
  <meta name=""viewport"" content=""width=device-width,initial-scale=1"">
  <style>
    body{font-family:Inter,system-ui,Segoe UI,Roboto,Helvetica,Arial;margin:2rem}
    textarea{width:100%;height:160px;font-family:monospace;font-size:14px}
    pre{background:#f4f4f4;padding:1rem;border-radius:6px;overflow:auto}
    .controls{display:flex;gap:8px;margin-top:8px}
  </style>
</head>
<body>
  <h1>Azure DevOps YAML Generator Bot</h1>
  <p>Paste a repository URL or type a prompt like:<br><code>Generate pipeline for https://github.com/org/repo includeSonar=true includeFortify=false</code></p>
  <textarea id=""prompt"">Generate pipeline for https://github.com/your-org/your-repo includeSonar=true includeFortify=true</textarea>
  <div class=""controls""><button id=""run"">Generate YAML</button><span id=""status""></span></div>
  <h3>Generated YAML</h3>
  <pre id=""output"">(results will appear here)</pre>
<script>
document.getElementById('run').addEventListener('click', async ()=>{
  const prompt = document.getElementById('prompt').value;
  document.getElementById('status').textContent = 'Generating...';
  try{
    const res = await fetch('/api/generate',{method:'POST',body:prompt});
    const data = await res.json();
    if(res.ok){
      document.getElementById('output').textContent = data.yaml;
    } else {
      document.getElementById('output').textContent = JSON.stringify(data,null,2);
    }
  }catch(e){document.getElementById('output').textContent = e.toString();}
  document.getElementById('status').textContent = '';
});
</script>
</body>
</html>");

    }
}

public class PipelineGenerator
{
    private readonly ILogger _logger;
    public PipelineGenerator(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<string> GeneratePipelineForRepositoryAsync(string repoUrl, bool includeSonar, bool includeFortify)
    {
        // 1) Clone the repo to a temp folder (shallow)
        var temp = Path.Combine(Path.GetTempPath(), "repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        _logger.LogInformation("Cloning into {dir}", temp);
        var cloneSuccess = await RunGitCloneAsync(repoUrl, temp);
        if (!cloneSuccess) throw new Exception("Failed to clone repository. Ensure git is installed and URL is accessible.");

        try
        {
            // 2) Detect languages
            var languages = DetectLanguages(temp);
            _logger.LogInformation("Detected languages: {langs}", string.Join(",", languages));

            // 3) Build YAML from templates
            var yaml = BuildAzurePipelineYaml(languages, includeSonar, includeFortify);
            return yaml;
        }
        finally
        {
            // cleanup - try best-effort
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private async Task<bool> RunGitCloneAsync(string repoUrl, string destination)
    {
        // Use 'git clone --depth 1 <repo> <destination>'
        try
        {
            var psi = new ProcessStartInfo("git", $"clone --depth 1 \"{repoUrl}\" \"{destination}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            _logger.LogInformation(stdout);
            if (p.ExitCode != 0)
            {
                _logger.LogWarning("git clone returned {code} stderr: {err}", p.ExitCode, stderr);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while running git clone");
            return false;
        }
    }

    private List<string> DetectLanguages(string repoPath)
    {
        // Simple detection by file extensions.
        var extToLang = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {".cs","dotnet"},
            {".fs","dotnet"},
            {".vb","dotnet"},
            {".py","python"},
            {".js","node"},
            {".ts","node"},
            {".java","java"},
            {"pom.xml","java"},
            {"build.gradle","java"},
            {"package.json","node"},
            {"requirements.txt","python"},
            {"setup.py","python"}
        };

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var ext = Path.GetExtension(file);
            if (extToLang.TryGetValue(name, out var langByName)) { found.Add(langByName); continue; }
            if (extToLang.TryGetValue(ext, out var lang)) { found.Add(lang); }
        }

        if (found.Count == 0) found.Add("dotnet"); // default to dotnet
        return found.ToList();
    }

    private string BuildAzurePipelineYaml(List<string> languages, bool includeSonar, bool includeFortify)
    {
        // Build a multi-stage Azure DevOps pipeline supporting build/test/publish for the detected languages.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("trigger:\n  branches:\n    include:\n      - main\n      - master\n");
        sb.AppendLine("pr:\n  branches:\n    include:\n      - '*'");
        sb.AppendLine();
        sb.AppendLine("stages:");

        foreach (var lang in languages)
        {
            switch (lang.ToLowerInvariant())
            {
                case "dotnet":
                    sb.AppendLine(GenerateDotNetStage(includeSonar, includeFortify));
                    break;
                case "python":
                    sb.AppendLine(GeneratePythonStage(includeSonar, includeFortify));
                    break;
                case "node":
                    sb.AppendLine(GenerateNodeStage(includeSonar, includeFortify));
                    break;
                case "java":
                    sb.AppendLine(GenerateJavaStage(includeSonar, includeFortify));
                    break;
                default:
                    sb.AppendLine($"# Unknown language: {lang} - no stage generated\n");
                    break;
            }
        }

        // Optionally include an orchestration stage for deployment (placeholder)
        sb.AppendLine(@"- stage: Deploy
  displayName: 'Deploy (placeholder)'
  dependsOn: []
  jobs:
  - deployment: DeployJob
    environment: 'dev'
    strategy:
      runOnce:
        deploy:
          steps:
          - script: echo 'Add deployment tasks (ARM/Bicep/Helm/Kubernetes) here'");

        return sb.ToString();
    }

    private string GenerateDotNetStage(bool sonar, bool fortify)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("- stage: Build_DotNet");
        sb.AppendLine("  displayName: 'Build & Test (.NET)'");
        sb.AppendLine("  jobs:");
        sb.AppendLine("  - job: Build");
        sb.AppendLine("    pool:\n      vmImage: 'windows-latest'\n    steps:");
        sb.AppendLine("    - task: UseDotNet@2\n      inputs:\n        packageType: 'sdk'\n        version: '8.x'\n        includePreview: true");
        sb.AppendLine("    - script: dotnet restore\n      displayName: 'dotnet restore'");
        sb.AppendLine("    - script: dotnet build --configuration Release --no-restore\n      displayName: 'dotnet build'");
        sb.AppendLine("    - script: dotnet test --configuration Release --no-build --verbosity normal\n      displayName: 'dotnet test'");

        if (sonar)
        {
            sb.AppendLine();
            sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud' # service connection name\n        organization: 'your-org'\n        scannerMode: 'MSBuild'\n        projectKey: 'your-project-key'\n        projectName: 'your-project-name'");
            sb.AppendLine("    - script: dotnet build\n      displayName: 'dotnet build (for Sonar)'\n");
            sb.AppendLine("    - task: SonarCloudAnalyze@1");
            sb.AppendLine("    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
        }

        if (fortify)
        {
            sb.AppendLine();
            sb.AppendLine("    - task: PowerShell@2\n      displayName: 'Run Fortify SAST Scan (example)'");
            sb.AppendLine("      inputs:\n        targetType: 'inline'\n        script: |\n          Write-Host 'This is a placeholder for Fortify scan steps.\nYou should invoke Fortify SCA (sourceanalyzer) here, upload results to SSC, and fail/mark build accordingly.'");
        }

        sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

        return sb.ToString();
    }

    private string GenerateNodeStage(bool sonar, bool fortify)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("- stage: Build_Node");
        sb.AppendLine("  displayName: 'Build & Test (Node.js)'");
        sb.AppendLine("  jobs:");
        sb.AppendLine("  - job: Build");
        sb.AppendLine("    pool:\n      vmImage: 'ubuntu-latest'\n    steps:");
        sb.AppendLine("    - task: NodeTool@0\n      inputs:\n        versionSpec: '18.x'\n        checkLatest: true");
        sb.AppendLine("    - script: npm install\n      displayName: 'npm install'");
        sb.AppendLine("    - script: npm test || true\n      displayName: 'npm test (continue on failures)'");

        if (sonar)
        {
            sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud'\n        organization: 'your-org'\n        scannerMode: 'CLI'\n        configMode: 'manual'\n        cliProjectKey: 'your-project-key'\n        cliProjectName: 'your-project-name'");
            sb.AppendLine("    - script: npm run build || true\n      displayName: 'npm build (for Sonar)'");
            sb.AppendLine("    - task: SonarCloudAnalyze@1\n    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
        }

        if (fortify)
        {
            sb.AppendLine("    - script: echo 'Run Fortify SCA for Node (placeholder)'");
        }

        sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

        return sb.ToString();
    }

    private string GeneratePythonStage(bool sonar, bool fortify)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("- stage: Build_Python");
        sb.AppendLine("  displayName: 'Build & Test (Python)'");
        sb.AppendLine("  jobs:");
        sb.AppendLine("  - job: Build");
        sb.AppendLine("    pool:\n      vmImage: 'ubuntu-latest'\n    steps:");
        sb.AppendLine("    - task: UsePythonVersion@0\n      inputs:\n        versionSpec: '3.10'\n        addToPath: true");
        sb.AppendLine("    - script: python -m pip install -r requirements.txt || true\n      displayName: 'pip install'");
        sb.AppendLine("    - script: pytest -q || true\n      displayName: 'pytest'");

        if (sonar)
        {
            sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud'\n        organization: 'your-org'\n        scannerMode: 'CLI'\n        configMode: 'manual'\n        cliProjectKey: 'your-project-key'\n        cliProjectName: 'your-project-name'");
            sb.AppendLine("    - script: sonar-scanner\n      displayName: 'Run SonarScanner'\n");
            sb.AppendLine("    - task: SonarCloudAnalyze@1\n    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
        }

        if (fortify)
        {
            sb.AppendLine("    - script: echo 'Run Fortify SCA for Python (placeholder)'");
        }

        sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

        return sb.ToString();
    }

    private string GenerateJavaStage(bool sonar, bool fortify)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("- stage: Build_Java");
        sb.AppendLine("  displayName: 'Build & Test (Java)'");
        sb.AppendLine("  jobs:");
        sb.AppendLine("  - job: Build");
        sb.AppendLine("    pool:\n      vmImage: 'ubuntu-latest'\n    steps:");
        sb.AppendLine("    - task: MavenAuthenticate@0\n      inputs:\n        artifactsFeeds: ''");
        sb.AppendLine("    - script: mvn -B -DskipTests=false package\n      displayName: 'mvn package'");

        if (sonar)
        {
            sb.AppendLine("    - task: SonarCloudPrepare@1\n      inputs:\n        SonarCloud: 'SonarCloud'\n        organization: 'your-org'\n        scannerMode: 'CLI'\n        configMode: 'manual'\n        cliProjectKey: 'your-project-key'\n        cliProjectName: 'your-project-name'");
            sb.AppendLine("    - script: mvn sonar:sonar\n      displayName: 'mvn sonar:sonar'");
            sb.AppendLine("    - task: SonarCloudAnalyze@1\n    - task: SonarCloudPublish@1\n      inputs:\n        pollingTimeoutSec: '300'");
        }

        if (fortify)
        {
            sb.AppendLine("    - script: echo 'Run Fortify SCA for Java (placeholder)'");
        }

        sb.AppendLine("    - task: PublishBuildArtifacts@1\n      inputs:\n        PathtoPublish: '$(Build.ArtifactStagingDirectory)'\n        ArtifactName: 'drop'\n        publishLocation: 'Container'");

        return sb.ToString();
    }
}
