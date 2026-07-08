using RagNet.Mcp.Analyzers.JavaScriptTypeScript;
using RagNet.Mcp.Indexing;

namespace RagNet.Mcp.Tests;

public sealed class JavaScriptTypeScriptAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_PreservesImportsExportsComponentsAndRoutes()
    {
        using var workspace = new TemporaryWorkspace();
        var file = workspace.WriteFile(
            "src/App.tsx",
            """
            import React from "react";
            import { Route } from "react-router-dom";

            export type ViewModel = { title: string };

            export const App = ({ title }: ViewModel) => {
                return (
                    <Routes>
                        <Route path="/dashboard" element={<Dashboard title={title} />} />
                    </Routes>
                );
            };

            export function loadDashboard(id: string) {
                return fetch(`/api/dashboard/${id}`);
            }

            class DashboardService {
                findAll() {
                    return [];
                }
            }
            """);

        var analyzer = new JavaScriptTypeScriptAnalyzer();
        var chunks = await analyzer.AnalyzeAsync(workspace.RootPath, file);

        var dependencies = Assert.Single(chunks, chunk => chunk.SymbolName == "module dependencies");
        Assert.Equal("ImportsExports", dependencies.SymbolKind);
        Assert.Contains("import React", dependencies.Content);
        Assert.Contains("export type ViewModel", dependencies.Content);

        var component = Assert.Single(chunks, chunk => chunk.SymbolName == "App");
        Assert.Equal("Component", component.SymbolKind);
        Assert.Equal(IndexedContentTypes.Markup, component.ContentType);
        Assert.Contains("<Route path=\"/dashboard\"", component.Content);

        Assert.Contains(chunks, chunk => chunk.SymbolName == "route /dashboard" && chunk.SymbolKind == "Route");
        Assert.Contains(chunks, chunk => chunk.SymbolName == "loadDashboard" && chunk.SymbolKind == "Function");
        Assert.Contains(chunks, chunk => chunk.SymbolName == "DashboardService" && chunk.SymbolKind == "Class");
    }

    [Fact]
    public async Task AnalyzeAsync_CanAnalyzeConfiguredScriptExtensions()
    {
        var analyzer = new JavaScriptTypeScriptAnalyzer();

        Assert.True(analyzer.CanAnalyze("index.js"));
        Assert.True(analyzer.CanAnalyze("component.jsx"));
        Assert.True(analyzer.CanAnalyze("module.mjs"));
        Assert.True(analyzer.CanAnalyze("config.cjs"));
        Assert.True(analyzer.CanAnalyze("types.ts"));
        Assert.True(analyzer.CanAnalyze("view.tsx"));
        Assert.True(analyzer.CanAnalyze("module.mts"));
        Assert.True(analyzer.CanAnalyze("config.cts"));
        Assert.False(analyzer.CanAnalyze("Program.cs"));

        await Task.CompletedTask;
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), $"ragnet-tests-{Guid.NewGuid():N}");

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(RootPath);
        }

        public string WriteFile(string relativePath, string content)
        {
            var file = Path.Combine(RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
