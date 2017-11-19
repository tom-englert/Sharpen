﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.LanguageServices;
using Sharpen.Engine.Extensions;
using Sharpen.Engine.SharpenSuggestions.CSharp60;
using Sharpen.Engine.SharpenSuggestions.CSharp70;

namespace Sharpen.Engine
{
    public class SharpenEngine
    {
        private static readonly ISharpenSuggestion[] Suggestions =
        {
            // C# 6.0.
            UseExpressionBodyForGetOnlyProperties.Instance,
            UseExpressionBodyForGetOnlyIndexers.Instance,
            // C# 7.0.
            UseExpressionBodyForConstructors.Instance,
            UseExpressionBodyForDestructors.Instance,
            UseExpressionBodyForGetAccessorsInProperties.Instance,
            UseExpressionBodyForGetAccessorsInIndexers.Instance,
            UseExpressionBodyForSetAccessorsInProperties.Instance,
            UseExpressionBodyForSetAccessorsInIndexers.Instance
        };

        // We want to avoid creation of a huge number of temporary Action objects
        // while invoking Parallel.Invoke().
        // That's why we precreate these Action objects and at the beginning of the
        // analysis create just once out of them Actions that are really used in
        // the Parallel.Invoke().
        private static Action<SyntaxTree, SingleSyntaxTreeAnalysisContext, ConcurrentBag<AnalysisResult>>[] AnalyzeSingleSyntaxTreeAndCollectResultsActions { get; } =
            Suggestions
                .OfType<ISingleSyntaxTreeAnalyzer>()
                .Select(analyzer => new Action<SyntaxTree, SingleSyntaxTreeAnalysisContext, ConcurrentBag<AnalysisResult>>((syntaxTree, analysisContext, results) =>
                {
                    foreach (var analysisResult in analyzer.Analyze(syntaxTree, analysisContext))
                    {
                        results.Add(analysisResult);
                    }
                }))
                .ToArray();

        public int GetAnalysisMaximumProgress(VisualStudioWorkspace visualStudioWorkspace)
        {
            if (!visualStudioWorkspace.IsSolutionOpen()) return 0;

            // So far, we will report the progress after a single document is fully analyzed.
            // Therefore, here we just have to count the documents using exactly the same filters
            // as in the AnalyzeSingleSyntaxTreesAsync().
            // BDW, the reuse of the filters in calculating the Analysis Maximum Progress and
            // in the analysis itself is the reason for the existinace of the two
            // ...Satisfies...Filter() methods.
            return visualStudioWorkspace
                .CurrentSolution
                .Projects
                .Where(ProjectSatisfiesProjectFilter)
                .SelectMany(project => project.Documents)
                .Count(DocumentSatisfiesDocumentFilter);
        }

        public Task<IEnumerable<AnalysisResult>> AnalyzeAsync(VisualStudioWorkspace visualStudioWorkspace, IProgress<int> progress)
        {
            return AnalyzeSingleSyntaxTreesAsync(visualStudioWorkspace, progress);
        }

        private static async Task<IEnumerable<AnalysisResult>> AnalyzeSingleSyntaxTreesAsync(VisualStudioWorkspace visualStudioWorkspace, IProgress<int> progress)
        {
            var analysisResults = new ConcurrentBag<AnalysisResult>();
            SyntaxTree syntaxTree = null;
            SingleSyntaxTreeAnalysisContext analysisContext = null;

            var analyseSyntaxTreeActions = AnalyzeSingleSyntaxTreeAndCollectResultsActions
                // We intentionally access the modified closure here (syntaxTree, analysisContext),
                // because we want to avoid creation of a huge number of temporary Action objects.

                // ReSharper disable AccessToModifiedClosure
                .Select(action => new Action(() => action(syntaxTree, analysisContext, analysisResults)))
                // ReSharper restore AccessToModifiedClosure
                .ToArray();

            // WARNING: Keep the progress counter in sync with the logic behind the calculation of the maximum progress!
            int progressCounter = 0;
            foreach (var project in visualStudioWorkspace.CurrentSolution.Projects.Where(ProjectSatisfiesProjectFilter))
            {                
                foreach (var document in project.Documents.Where(DocumentSatisfiesDocumentFilter))
                {
                    analysisContext = new SingleSyntaxTreeAnalysisContext(document);

                    syntaxTree = await document.GetSyntaxTreeAsync().ConfigureAwait(false);
                    // Each of the actions will operate on the same (current) syntaxTree.
                    Parallel.Invoke(analyseSyntaxTreeActions);
                    progress.Report(++progressCounter);
                }
            }
            return analysisResults;
        }

        private static bool ProjectSatisfiesProjectFilter(Project project)
        {
            return project.Language == "C#";
        }

        private static bool DocumentSatisfiesDocumentFilter(Document document)
        {
            return document.SupportsSyntaxTree && !IsGenerated(document);
        }

        // Hardcoded so far. In the future we will have this in Sharpen Settings, similar to the equivalent ReSharper settings.
        private static readonly string[] GeneratedDocumentsEndings = { ".Designer.cs", ".Generated.cs" };
        private static bool IsGenerated(Document document)
        {
            return GeneratedDocumentsEndings.Any(ending => document.FilePath.IndexOf(ending, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}