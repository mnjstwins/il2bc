﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Collections;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    /// <summary>
    /// A proposed replacement for AnalyzerDriver that uses a <see cref="AsyncQueue{TElement}"/> of <see cref="CompilationEvent"/>s to drive its analysis.
    /// </summary>
    public abstract class AnalyzerDriver3 : IDisposable
    {
        readonly TimeSpan timeoutPeriod = TimeSpan.FromMinutes(2); // 2 minutes timeout. TODO: configurable?
        private static readonly ConditionalWeakTable<Compilation, SuppressMessageAttributeState> suppressMessageStateByCompilation = new ConditionalWeakTable<Compilation, SuppressMessageAttributeState>();

        private const string DiagnosticId = "AnalyzerDriver";
        private readonly Action<Diagnostic> addDiagnostic;
        private Compilation compilation;
        internal bool continueOnError = true; // should be a parameter?
        private ImmutableArray<Task> workers;
        private ImmutableArray<Task> syntaxAnalyzers;

        // TODO: should these be made lazy?
        internal ImmutableArray<IDiagnosticAnalyzer> analyzers;
        private ImmutableArray<ICodeBlockStartedAnalyzer> bodyAnalyzers;
        private ImmutableArray<ISemanticModelAnalyzer> semanticModelAnalyzers;
        private ImmutableArray<ImmutableArray<ISymbolAnalyzer>> declarationAnalyzersByKind; // indexed by symbol kind (of interest)
        internal ImmutableArray<ICodeBlockStartedAnalyzer> codeBlockStartedAnalyzers;
        internal ImmutableArray<ICodeBlockEndedAnalyzer> codeBlockEndedAnalyzers;
        private Task initialWorker;
        protected AnalyzerOptions analyzerOptions;

        /// <summary>
        /// The compilation queue to create the compilation with via WithEventQueue.
        /// </summary>
        public AsyncQueue<CompilationEvent> CompilationEventQueue
        {
            get;
            private set;
        }

        /// <summary>
        /// An async queue that is fed the diagnostics as they are computed.
        /// </summary>
        public AsyncQueue<Diagnostic> DiagnosticQueue
        {
            get;
            private set;
        }

        internal static Compilation AttachAnalyzerDriverToCompilation(Compilation compilation, ImmutableArray<IDiagnosticAnalyzer> analyzers, out AnalyzerDriver3 analyzerDriver3, AnalyzerOptions options, CancellationToken cancellationToken)
        {
            analyzerDriver3 = compilation.AnalyzerForLanguage(analyzers, options, cancellationToken);
            return compilation.WithEventQueue(analyzerDriver3.CompilationEventQueue);
        }

        /// <summary>
        /// Create an analyzer driver.
        /// </summary>
        /// <param name="analyzers">The set of analyzers to include in the analysis</param>
        /// <param name="options">Options that are passed to analyzers</param>
        /// <param name="cancellationToken">a cancellation token that can be used to abort analysis</param>
        protected AnalyzerDriver3(ImmutableArray<IDiagnosticAnalyzer> analyzers, AnalyzerOptions options, CancellationToken cancellationToken)
        {
            CompilationEventQueue = new AsyncQueue<CompilationEvent>();
            DiagnosticQueue = new AsyncQueue<Diagnostic>();
            addDiagnostic = GetDiagnosticSinkWithSuppression();
            analyzerOptions = options;

            // start the first task to drain the event queue. The first compilation event is to be handled before
            // any other ones, so we cannot have more than one event processing task until the first event has been handled.
            initialWorker = Task.Run(async () =>
            {
                try
                {
                    await InitialWorker(analyzers, continueOnError, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // If creation is cancelled we had better not use the driver any longer
                    this.Dispose();
                }
            });
        }

        /// <summary>
        /// Returns all diagnostics computed by the analyzers since the last time this was invoked.
        /// </summary>
        public async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync()
        {
            var allDiagnostics = DiagnosticBag.GetInstance();
            if (CompilationEventQueue.IsCompleted)
            {
                Task completed = DiagnosticQueue.WhenCompleted;
                Task timeout = Task.Delay(timeoutPeriod);
                if (await Task.WhenAny(completed, timeout).ConfigureAwait(false) == timeout)
                {
                    var desc = new DiagnosticDescriptor(DiagnosticId,
                          CodeAnalysisResources.CompilerAnalyzerFailure,
                          "event queue completed but not diagnostic queue after " + timeoutPeriod,
                          category: Diagnostic.CompilerDiagnosticCategory,
                          defaultSeverity: DiagnosticSeverity.Error,
                          isEnabledByDefault: true);
                    allDiagnostics.Add(Diagnostic.Create(desc, Location.None));
                }
            }

            Diagnostic d;
            while (DiagnosticQueue.TryDequeue(out d))
            {
                allDiagnostics.Add(d);
            }

            if (compilation != null)
            {
                var filteredDiagnostics = DiagnosticBag.GetInstance();
                compilation.FilterAndAppendAndFreeDiagnostics(filteredDiagnostics, ref allDiagnostics);
                return filteredDiagnostics.ToReadOnlyAndFree();
            }
            else
            {
                return allDiagnostics.ToReadOnlyAndFree();
            }
        }

        /// <summary>
        /// Return a task that completes when the driver is done producing diagnostics.
        /// </summary>
        public async Task WhenCompleted()
        {
            Task completed = CompilationEventQueue.WhenCompleted;
            Task timeout = Task.Delay(timeoutPeriod);
            if (await Task.WhenAny(completed, timeout).ConfigureAwait(false) == timeout)
            {
                var desc = new DiagnosticDescriptor(DiagnosticId,
                      CodeAnalysisResources.CompilerAnalyzerFailure,
                      "compilation event queue not completed after " + timeoutPeriod,
                      category: Diagnostic.CompilerDiagnosticCategory,
                      defaultSeverity: DiagnosticSeverity.Error,
                      isEnabledByDefault: true);
                addDiagnostic(Diagnostic.Create(desc, Location.None));
            }

            Task timeout2 = Task.Delay(timeoutPeriod);
            if (await Task.WhenAny(Task.WhenAll(workers), timeout2).ConfigureAwait(false) == timeout2)
            {
                var desc = new DiagnosticDescriptor(DiagnosticId,
                      CodeAnalysisResources.CompilerAnalyzerFailure,
                      "workers not completed after " + timeoutPeriod,
                      category: Diagnostic.CompilerDiagnosticCategory,
                      defaultSeverity: DiagnosticSeverity.Error,
                      isEnabledByDefault: true);
                addDiagnostic(Diagnostic.Create(desc, Location.None));
            }
        }

        private async Task InitialWorker(ImmutableArray<IDiagnosticAnalyzer> analyzers, bool continueOnError, CancellationToken cancellationToken)
        {
            // Pull out the first event, which should be the "start compilation" event.
            var firstEvent = await CompilationEventQueue.DequeueAsync(/*cancellationToken*/).ConfigureAwait(false);
            var startCompilation = firstEvent as CompilationStartedEvent;
            if (startCompilation == null)
            {
                // The queue contents are ill formed, as they do not start with a CompilationStarted event.
                // Throwing an exception here won't do much good, as there is nothing higher on the call stack.
                // So we instead complete the queue so that the caller does not enqueue further data.
                CompilationEventQueue.Complete();
                DiagnosticQueue.Complete();
                CompilationEvent drainedEvent;
                while (CompilationEventQueue.TryDequeue(out drainedEvent)) { }
                Debug.Assert(false, "First event must be CompilationStartedEvent, not " + firstEvent.GetType().Name);
            }

            var compilation = startCompilation.Compilation;
            Interlocked.CompareExchange(ref this.compilation, compilation, null);

            // Compute the set of effective analyzers based on suppression, and running the initial analyzers
            var effectiveAnalyzers = GetEffectiveAnalyzers(analyzers, compilation, analyzerOptions, addDiagnostic, continueOnError, cancellationToken);

            ImmutableInterlocked.InterlockedInitialize(ref this.analyzers, effectiveAnalyzers);
            ImmutableInterlocked.InterlockedInitialize(ref declarationAnalyzersByKind, MakeDeclarationAnalyzersByKind());
            ImmutableInterlocked.InterlockedInitialize(ref bodyAnalyzers, analyzers.OfType<ICodeBlockStartedAnalyzer>().ToImmutableArray());
            ImmutableInterlocked.InterlockedInitialize(ref semanticModelAnalyzers, analyzers.OfType<ISemanticModelAnalyzer>().ToImmutableArray());
            ImmutableInterlocked.InterlockedInitialize(ref codeBlockStartedAnalyzers, analyzers.OfType<ICodeBlockStartedAnalyzer>().ToImmutableArray());
            ImmutableInterlocked.InterlockedInitialize(ref codeBlockEndedAnalyzers, analyzers.OfType<ICodeBlockEndedAnalyzer>().ToImmutableArray());

            // Invoke the syntax tree analyzers
            // TODO: How can the caller restrict this to one or a set of trees, or a span in a tree, rather than all trees in the compilation?
            var syntaxAnalyzers = ArrayBuilder<Task>.GetInstance();
            foreach (var tree in compilation.SyntaxTrees)
            {
                foreach (var a in analyzers.OfType<ISyntaxTreeAnalyzer>())
                {
                    var runningAsynchronously = Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Catch Exception from a.AnalyzeSyntaxTree
                        ExecuteAndCatchIfThrows(a, addDiagnostic, continueOnError, cancellationToken, () => { a.AnalyzeSyntaxTree(tree, addDiagnostic, analyzerOptions, cancellationToken); });
                    });

                    syntaxAnalyzers.Add(runningAsynchronously);
                }
            }

            ImmutableInterlocked.InterlockedInitialize(ref this.syntaxAnalyzers, syntaxAnalyzers.ToImmutableAndFree());

            // start some tasks to drain the event queue
            cancellationToken.ThrowIfCancellationRequested();
            const int nTasks = 1;
            var workers = ArrayBuilder<Task>.GetInstance();
            for (int i = 0; i < nTasks; i++)
            {
                workers.Add(Task.Run(() => ProcessCompilationEvents(cancellationToken)));
            }

            ImmutableInterlocked.InterlockedInitialize(ref this.workers, workers.ToImmutableAndFree());

            // TODO: Analyze nodes for those parts of each syntax tree that are not inside declarations that are analyzed.
            // For example, compilation units and namespaces, usings, etc. Perhaps those should be processed here?
        }

        private ImmutableArray<ImmutableArray<ISymbolAnalyzer>> MakeDeclarationAnalyzersByKind()
        {
            var analyzersByKind = new List<ArrayBuilder<ISymbolAnalyzer>>();
            foreach (var analyzer in analyzers.OfType<ISymbolAnalyzer>())
            {
                // catch exceptions from SymbolKindsOfInterest
                ExecuteAndCatchIfThrows(analyzer, addDiagnostic, continueOnError, default(CancellationToken), () =>
                {
                    var kinds = analyzer.SymbolKindsOfInterest;
                    foreach (var k in kinds.Distinct())
                    {
                        if ((int)k > 100) continue; // protect against vicious analyzers
                        while ((int)k >= analyzersByKind.Count)
                        {
                            analyzersByKind.Add(ArrayBuilder<ISymbolAnalyzer>.GetInstance());
                        }

                        analyzersByKind[(int)k].Add(analyzer);
                    }
                });
            }

            return analyzersByKind.Select(a => a.ToImmutableAndFree()).ToImmutableArray();
        }

        private async void ProcessCompilationEvents(CancellationToken cancellationToken)
        {
            try
            {
                await ProcessEvents(cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // when the queue is Completed the awaiting tasks get cancelled.
                // In that case we just return from this task
            }
        }

        private async Task ProcessEvents(CancellationToken cancellationToken)
        {
            while (true)
            {
                try
                {
                    var e = await CompilationEventQueue.DequeueAsync(/*cancellationToken*/).ConfigureAwait(false);
                    await ProcessEvent(e, cancellationToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // when the task is cancelled we stop processing events.
                    // the caller catches this.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // when just a single operation is cancelled, we continue processing events.
                    // TODO: what is the desired behavior in this case?
                }
                catch (Exception ex)
                {
                    var desc = new DiagnosticDescriptor(DiagnosticId,
                          CodeAnalysisResources.CompilerAnalyzerFailure,
                          "diagnostic analyzer worker threw an exception " + ex,
                          category: Diagnostic.CompilerDiagnosticCategory,
                          defaultSeverity: DiagnosticSeverity.Error,
                          isEnabledByDefault: true);
                    addDiagnostic(Diagnostic.Create(desc, Location.None));
                }
            }
        }

        private async Task ProcessEvent(CompilationEvent e, CancellationToken cancellationToken)
        {
            var symbolEvent = e as SymbolDeclaredCompilationEvent;
            if (symbolEvent != null)
            {
                await ProcessSymbolDeclared(symbolEvent, cancellationToken).ConfigureAwait(false);
                return;
            }

            var completedEvent = e as CompilationUnitCompletedEvent;
            if (completedEvent != null)
            {
                await ProcessCompilationUnitCompleted(completedEvent, cancellationToken).ConfigureAwait(false);
                return;
            }

            var endEvent = e as CompilationCompletedEvent;
            if (endEvent != null)
            {
                await ProcessCompilationCompleted(endEvent, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Unexpected compilation event of type " + e.GetType().Name);
        }

        private Task ProcessSymbolDeclared(SymbolDeclaredCompilationEvent symbolEvent, CancellationToken cancellationToken)
        {
            try
            {
                return AnalyzeSymbol(symbolEvent, cancellationToken);
            }
            finally
            {
                symbolEvent.FlushCache();
            }
        }

        private Task AnalyzeSymbol(SymbolDeclaredCompilationEvent symbolEvent, CancellationToken cancellationToken)
        {
            var symbol = symbolEvent.Symbol;
            Action<Diagnostic> addDiagnosticForSymbol = GetDiagnosticSinkWithSuppression(symbol);
            var tasks = ArrayBuilder<Task>.GetInstance();
            if ((int)symbol.Kind < declarationAnalyzersByKind.Length)
            {
                foreach (var da in declarationAnalyzersByKind[(int)symbol.Kind])
                {
                    // TODO: is the overhead of creating tasks here too high compared to the cost of running them sequentially?
                    tasks.Add(Task.Run(() =>
                    {
                        // Catch Exception from da.AnalyzeSymbol
                        ExecuteAndCatchIfThrows(da, addDiagnostic, continueOnError, cancellationToken, () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            da.AnalyzeSymbol(symbol, compilation, addDiagnosticForSymbol, this.analyzerOptions, cancellationToken);
                        });
                    }));
                }
            }

            // TODO: what about syntax references elsewhere, for example in a class base clause?
            switch (symbol.Kind)
            {
                // TODO: what about other syntax, such as base clauses, using directives, top-level attributes, etc?
                case SymbolKind.Method:
                case SymbolKind.Field:
                case SymbolKind.Event: // TODO: should this be restricted to field-like events?
                    foreach (var decl in symbol.DeclaringSyntaxReferences)
                    {
                        tasks.Add(AnalyzeDeclaringReference(symbolEvent, decl, addDiagnostic, cancellationToken));
                    }
                    break;
            }

            return Task.WhenAll(tasks.ToImmutableAndFree());
        }

        protected abstract Task AnalyzeDeclaringReference(SymbolDeclaredCompilationEvent symbolEvent, SyntaxReference decl, Action<Diagnostic> addDiagnostic, CancellationToken cancellationToken);

        private Task ProcessCompilationUnitCompleted(CompilationUnitCompletedEvent completedEvent, CancellationToken cancellationToken)
        {
            // When the compiler is finished with a compilation unit, we can run user diagnostics which
            // might want to ask the compiler for all the diagnostics in the source file, for example
            // to get information about unnecessary usings.

            try
            {
                var tasks = ArrayBuilder<Task>.GetInstance();
                var semanticModel = completedEvent.SemanticModel;
                foreach (var da in semanticModelAnalyzers)
                {
                    // TODO: is the overhead of creating tasks here too high compared to the cost of running them sequentially?
                    tasks.Add(Task.Run(() =>
                    {
                        // Catch Exception from da.AnalyzeSemanticModel
                        ExecuteAndCatchIfThrows(da, addDiagnostic, continueOnError, cancellationToken, () =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            da.AnalyzeSemanticModel(semanticModel, addDiagnostic, this.analyzerOptions, cancellationToken);
                        });
                    }));
                }

                return Task.WhenAll(tasks.ToImmutableAndFree());
            }
            finally
            {
                completedEvent.FlushCache();
            }
        }

        private async Task ProcessCompilationCompleted(CompilationCompletedEvent endEvent, CancellationToken cancellationToken)
        {
            var tasks = ArrayBuilder<Task>.GetInstance();
            foreach (var da in analyzers.OfType<ICompilationEndedAnalyzer>())
            {
                // TODO: is the overhead of creating tasks here too high compared to the cost of running them sequentially?
                tasks.Add(Task.Run(() =>
                {
                    // Catch Exception from da.OnCompilationEnded
                    ExecuteAndCatchIfThrows(da, addDiagnostic, continueOnError, cancellationToken, () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        da.OnCompilationEnded(compilation, addDiagnostic, this.analyzerOptions, cancellationToken);
                    });
                }));
            }

            await Task.WhenAll(tasks.Concat(this.syntaxAnalyzers)).ConfigureAwait(false);
            DiagnosticQueue.Complete();
        }

        private Action<Diagnostic> GetDiagnosticSinkWithSuppression(ISymbol symbolOpt = null)
        {
            return diagnostic =>
            {
                var d = compilation.FilterDiagnostic(diagnostic);
                if (d != null)
                {
                    var suppressMessageState = suppressMessageStateByCompilation.GetValue(compilation, (c) => new SuppressMessageAttributeState(c));
                    if (!suppressMessageState.IsDiagnosticSuppressed(d.Id, locationOpt: d.Location, symbolOpt: symbolOpt))
                    {
                        DiagnosticQueue.Enqueue(d);
                    }
                }
            };
        }

        /// <summary>
        /// Given a set of compiler or <see cref="IDiagnosticAnalyzer"/> generated <paramref name="diagnostics"/>, returns the effective diagnostics after applying the below filters:
        /// 1) <see cref="CompilationOptions.SpecificDiagnosticOptions"/> specified for the given <paramref name="compilation"/>.
        /// 2) <see cref="CompilationOptions.GeneralDiagnosticOption"/> specified for the given <paramref name="compilation"/>.
        /// 3) Diagnostic suppression through applied <see cref="System.Diagnostics.CodeAnalysis.SuppressMessageAttribute"/>.
        /// 4) Pragma directives for the given <paramref name="compilation"/>.
        /// </summary>
        public static IEnumerable<Diagnostic> GetEffectiveDiagnostics(IEnumerable<Diagnostic> diagnostics, Compilation compilation)
        {
            if (diagnostics == null)
            {
                throw new ArgumentNullException("diagnostics");
            }

            if (compilation == null)
            {
                throw new ArgumentNullException("compilation");
            }

            var suppressMessageState = suppressMessageStateByCompilation.GetValue(compilation, (c) => new SuppressMessageAttributeState(c));
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic != null)
                {
                    var effectiveDiagnostic = compilation.FilterDiagnostic(diagnostic);
                    if (effectiveDiagnostic != null && !suppressMessageState.IsDiagnosticSuppressed(effectiveDiagnostic.Id, effectiveDiagnostic.Location))
                    {
                        yield return effectiveDiagnostic;
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if all the diagnostics that can be produced by this analyzer are suppressed through options.
        /// <paramref name="continueOnError"/> says whether the caller would like the exception thrown by the analyzers to be handled or not. If true - Handles ; False - Not handled.
        /// </summary>
        public static bool IsDiagnosticAnalyzerSuppressed(IDiagnosticAnalyzer analyzer, CompilationOptions options, bool continueOnError = true)
        {
            if (analyzer == null)
            {
                throw new ArgumentNullException("analyzer");
            }

            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            Action<Diagnostic> dummy = _ => { };
            return IsDiagnosticAnalyzerSuppressed(analyzer, options, dummy, continueOnError, CancellationToken.None);
        }

        private static ImmutableArray<IDiagnosticAnalyzer> GetEffectiveAnalyzers(IEnumerable<IDiagnosticAnalyzer> analyzers, Compilation compilation, AnalyzerOptions analyzerOptions, Action<Diagnostic> addDiagnostic, bool continueOnError, CancellationToken cancellationToken)
        {
            var effectiveAnalyzers = ImmutableArray.CreateBuilder<IDiagnosticAnalyzer>();
            foreach (var analyzer in analyzers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDiagnosticAnalyzerSuppressed(analyzer, compilation.Options, addDiagnostic, continueOnError, cancellationToken))
                {
                    effectiveAnalyzers.Add(analyzer);
                    var startAnalyzer = analyzer as ICompilationStartedAnalyzer;
                    if (startAnalyzer != null)
                    {
                        ExecuteAndCatchIfThrows(startAnalyzer, addDiagnostic, continueOnError, cancellationToken, () =>
                        {
                            var compilationAnalyzer = startAnalyzer.OnCompilationStarted(compilation, addDiagnostic, analyzerOptions, cancellationToken);
                            if (compilationAnalyzer != null) effectiveAnalyzers.Add(compilationAnalyzer);
                        });
                    }
                }
            }

            return effectiveAnalyzers.ToImmutable();
        }

        /// <summary>
        /// Returns true if all the diagnostics that can be produced by this analyzer are suppressed through options.
        /// </summary>
        private static bool IsDiagnosticAnalyzerSuppressed(IDiagnosticAnalyzer analyzer, CompilationOptions options, Action<Diagnostic> addDiagnostic, bool continueOnError, CancellationToken cancellationToken)
        {
            var supportedDiagnostics = ImmutableArray<DiagnosticDescriptor>.Empty;

            // Catch Exception from analyzer.SupportedDiagnostics
            ExecuteAndCatchIfThrows(analyzer, addDiagnostic, continueOnError, cancellationToken, () => { supportedDiagnostics = analyzer.SupportedDiagnostics; });

            var diagnosticOptions = options.SpecificDiagnosticOptions;

            foreach (var diag in supportedDiagnostics)
            {
                // Is this diagnostic suppressed by default (as written by the rule author)
                var isSuppressed = !diag.IsEnabledByDefault;

                // If the user said something about it, that overrides the author.
                if (diagnosticOptions.ContainsKey(diag.Id))
                {
                    isSuppressed = diagnosticOptions[diag.Id] == ReportDiagnostic.Suppress;
                }

                if (isSuppressed)
                {
                    continue;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        protected static void ExecuteAndCatchIfThrows(IDiagnosticAnalyzer a, Action<Diagnostic> addDiagnostic, bool continueOnError, CancellationToken cancellationToken, Action analyze)
        {
            try
            {
                analyze();
            }
            catch (OperationCanceledException oce)
            {
                if (continueOnError)
                {
                    if (oce.CancellationToken != cancellationToken)
                    {
                        // Create a info diagnostic saying that the analyzer failed
                        addDiagnostic(GetAnalyzerDiagnostic(a, oce));
                    }
                }
            }
            catch (Exception e)
            {
                if (continueOnError)
                {
                    // Create a info diagnostic saying that the analyzer failed
                    addDiagnostic(GetAnalyzerDiagnostic(a, e));
                }
            }
        }

        internal static Diagnostic GetAnalyzerDiagnostic(IDiagnosticAnalyzer analyzer, Exception e)
        {
            return Diagnostic.Create(GetDiagnosticDescriptor(analyzer.GetType().ToString(), e.Message), Location.None);
        }

        private static DiagnosticDescriptor GetDiagnosticDescriptor(string analyzerName, string message)
        {
            return new DiagnosticDescriptor(DiagnosticId,
                CodeAnalysisResources.CompilerAnalyzerFailure,
                string.Format(CodeAnalysisResources.CompilerAnalyzerThrows, analyzerName, message),
                category: Diagnostic.CompilerDiagnosticCategory,
                defaultSeverity: DiagnosticSeverity.Info,
                isEnabledByDefault: true);
        }

        public void Dispose()
        {
            CompilationEventQueue.Complete();
            DiagnosticQueue.Complete();
        }
    }

    /// <summary>
    /// A proposed replacement for AnalyzerDriver that uses a <see cref="AsyncQueue{TElement}"/> of <see cref="CompilationEvent"/>s to drive its analysis.
    /// </summary>
    public class AnalyzerDriver3<TSyntaxKind> : AnalyzerDriver3
    {
        private Func<SyntaxNode, TSyntaxKind> GetKind;

        /// <summary>
        /// Create an analyzer driver.
        /// </summary>
        /// <param name="analyzers">The set of analyzers to include in the analysis</param>
        /// <param name="getKind">A delegate that returns the language-specific kind for a given syntax node</param>
        /// <param name="options">Options that are passed to analyzers</param>
        /// <param name="cancellationToken">a cancellation token that can be used to abort analysis</param>
        public AnalyzerDriver3(ImmutableArray<IDiagnosticAnalyzer> analyzers, Func<SyntaxNode, TSyntaxKind> getKind, AnalyzerOptions options, CancellationToken cancellationToken)
            : base(analyzers, options, cancellationToken)
        {
            GetKind = getKind;
        }

        protected override async Task AnalyzeDeclaringReference(SymbolDeclaredCompilationEvent symbolEvent, SyntaxReference decl, Action<Diagnostic> addDiagnostic, CancellationToken cancellationToken)
        {
            var symbol = symbolEvent.Symbol;
            var syntax = await decl.GetSyntaxAsync().ConfigureAwait(false);
            var endedAnalyzers = ArrayBuilder<ICodeBlockEndedAnalyzer>.GetInstance();
            endedAnalyzers.AddRange(codeBlockEndedAnalyzers);
            var nodeAnalyzers = ArrayBuilder<ISyntaxNodeAnalyzer<TSyntaxKind>>.GetInstance();
            nodeAnalyzers.AddRange(analyzers.OfType<ISyntaxNodeAnalyzer<TSyntaxKind>>());
            foreach (var da in codeBlockStartedAnalyzers)
            {
                // Catch Exception from da.OnCodeBlockStarted
                ExecuteAndCatchIfThrows(da, addDiagnostic, continueOnError, cancellationToken, () =>
                {
                    var blockStatefulAnalyzer = da.OnCodeBlockStarted(syntax, symbol, symbolEvent.SemanticModel(decl), addDiagnostic, analyzerOptions, cancellationToken);
                    var endedAnalyzer = blockStatefulAnalyzer as ICodeBlockEndedAnalyzer;
                    if (endedAnalyzer != null)
                    {
                        endedAnalyzers.Add(endedAnalyzer);
                    }
                    var nodeAnalyzer = blockStatefulAnalyzer as ISyntaxNodeAnalyzer<TSyntaxKind>;
                    if (nodeAnalyzer != null)
                    {
                        nodeAnalyzers.Add(nodeAnalyzer);
                    }
                });
            }

            PooledDictionary<TSyntaxKind, ArrayBuilder<ISyntaxNodeAnalyzer<TSyntaxKind>>> nodeAnalyzersByKind = null;
            foreach (var nodeAnalyzer in nodeAnalyzers)
            {
                // Catch Exception from  nodeAnalyzer.SyntaxKindsOfInterest
                try
                {
                    foreach (var kind in nodeAnalyzer.SyntaxKindsOfInterest)
                    {
                        if (nodeAnalyzersByKind == null) nodeAnalyzersByKind = PooledDictionary<TSyntaxKind, ArrayBuilder<ISyntaxNodeAnalyzer<TSyntaxKind>>>.GetInstance();
                        ArrayBuilder<ISyntaxNodeAnalyzer<TSyntaxKind>> analyzersForKind;
                        if (!nodeAnalyzersByKind.TryGetValue(kind, out analyzersForKind))
                        {
                            nodeAnalyzersByKind.Add(kind, analyzersForKind = ArrayBuilder<ISyntaxNodeAnalyzer<TSyntaxKind>>.GetInstance());
                        }
                        analyzersForKind.Add(nodeAnalyzer);
                    }
                }
                catch (Exception e)
                {
                    // Create a info diagnostic saying that the analyzer failed
                    addDiagnostic(GetAnalyzerDiagnostic(nodeAnalyzer, e));
                }
            }
            nodeAnalyzers.Free();

            SemanticModel semanticModel = (nodeAnalyzersByKind != null || endedAnalyzers.Any()) ? symbolEvent.SemanticModel(decl) : null;
            if (nodeAnalyzersByKind != null)
            {
                foreach (var child in syntax.DescendantNodesAndSelf())
                {
                    ArrayBuilder<ISyntaxNodeAnalyzer<TSyntaxKind>> analyzersForKind;
                    if (nodeAnalyzersByKind.TryGetValue(GetKind(child), out analyzersForKind))
                    {
                        foreach (var analyzer in analyzersForKind)
                        {
                            // Catch Exception from analyzer.AnalyzeNode
                            ExecuteAndCatchIfThrows(analyzer, addDiagnostic, continueOnError, cancellationToken, () => analyzer.AnalyzeNode(child, semanticModel, addDiagnostic, analyzerOptions, cancellationToken));
                        }
                    }
                }

                foreach (var b in nodeAnalyzersByKind.Values)
                {
                    b.Free();
                }

                nodeAnalyzersByKind.Free();
            }

            foreach (var a in endedAnalyzers)
            {
                // Catch Exception from a.OnCodeBlockEnded
                ExecuteAndCatchIfThrows(a, addDiagnostic, continueOnError, cancellationToken, () => a.OnCodeBlockEnded(syntax, symbol, semanticModel, addDiagnostic, analyzerOptions, cancellationToken));
            }

            endedAnalyzers.Free();
        }
    }
}
