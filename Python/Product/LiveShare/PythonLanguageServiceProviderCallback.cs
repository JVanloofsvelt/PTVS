// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Editor;
using Microsoft.PythonTools.Intellisense;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.LiveShare.LanguageServices;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudioTools;
using LSP = Microsoft.VisualStudio.LanguageServer.Protocol;
using LS = Microsoft.VisualStudio.LiveShare.LanguageServices;

namespace Microsoft.PythonTools.LiveShare {
    internal class PythonLanguageServiceProviderCallback : ILanguageServiceProviderCallback {
        private readonly IServiceProvider _serviceProvider;
        private readonly UIThreadBase _uiThread;

        // Cache analyzers for the session to avoid UI thread marshalling
        private readonly ConcurrentDictionary<Uri, VsProjectAnalyzer> _analyzerCache;

        public PythonLanguageServiceProviderCallback(IServiceProvider serviceProvider) {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _uiThread = _serviceProvider.GetUIThread();
            _analyzerCache = new ConcurrentDictionary<Uri, VsProjectAnalyzer>(UriEqualityComparer.Default);
        }

        private PythonLanguageServiceProviderCallback() {
            _analyzerCache = new ConcurrentDictionary<Uri, VsProjectAnalyzer>(UriEqualityComparer.Default);
        }

        /// <summary>
        /// Test helper factory for creating an instance with no service provider.
        /// </summary>
        internal static PythonLanguageServiceProviderCallback CreateTestInstance() => new PythonLanguageServiceProviderCallback();

#pragma warning disable 0067
        public event AsyncEventHandler<LanguageServiceNotifyEventArgs> NotifyAsync;
#pragma warning restore 0067

        private async Task<VsProjectAnalyzer> FindAnalyzerAsync(TextDocumentIdentifier document) {
            if (document?.Uri == null) {
                return null;
            }

            if (!_analyzerCache.TryGetValue(document.Uri, out var analyzer)) {
                var filePath = document.Uri.LocalPath;
                if (string.IsNullOrEmpty(filePath)) {
                    return null;
                }

                if (_uiThread != null) {
                    // TODO: Use URI for more accurate lookup
                    analyzer = await _uiThread.InvokeTask(async () =>
                        (await _serviceProvider.FindAllAnalyzersForFile(filePath)).FirstOrDefault() as VsProjectAnalyzer
                    );
                }

                analyzer = _analyzerCache.GetOrAdd(document.Uri, analyzer);
            }

            return analyzer;
        }

        /// <summary>
        /// Helper function for tests, enabling this class to be tested without needing
        /// a service provider or UI thread.
        /// </summary>
        internal void SetAnalyzer(Uri documentUri, VsProjectAnalyzer analyzer) {
            _analyzerCache[documentUri] = analyzer;
        }

        public async Task<TOut> RequestAsync<TIn, TOut>(LS.LspRequest<TIn, TOut> method, TIn param, RequestContext context, CancellationToken cancellationToken) {
            if (method.Name == Methods.Initialize.Name) {
                var capabilities = new ServerCapabilities {
                    CompletionProvider = new LSP.CompletionOptions {
                        TriggerCharacters = new[] { "." }
                    },
                    SignatureHelpProvider = new SignatureHelpOptions {
                        TriggerCharacters = new[] { "(", ",", ")" }
                    },
                    HoverProvider = true,
                    DefinitionProvider = true,
                    ReferencesProvider = true
                };
                object result = new InitializeResult { Capabilities = capabilities };
                return (TOut)(result);
            }

            if (method.Name == Methods.TextDocumentCompletion.Name ||
                method.Name == Methods.TextDocumentHover.Name ||
                method.Name == Methods.TextDocumentDefinition.Name ||
                method.Name == Methods.TextDocumentReferences.Name ||
                method.Name == Methods.TextDocumentSignatureHelp.Name
            ) {
                var doc = (param as TextDocumentPositionParams)?.TextDocument;
                if (doc == null) {
                    return default(TOut);
                }

                var analyzer = await FindAnalyzerAsync(doc);
                if (analyzer == null) {
                    return default(TOut);
                }

                if (method.Name == Methods.TextDocumentDefinition.Name) {
                    return (TOut)(object)await analyzer.SendLanguageServerRequestAsync<TIn, Location[]>(method.Name, param);
                }

                var entry = analyzer.GetAnalysisEntryFromUri(doc.Uri);
                if (entry != null) {
                    var buffers = entry.TryGetBufferParser()?.AllBuffers;
                    if (buffers != null) {
                        foreach (var b in buffers) {
                            await entry.EnsureCodeSyncedAsync(b);
                        }
                    }
                }

                return await analyzer.SendLanguageServerRequestAsync<TIn, TOut>(method.Name, param);
            }

            return default(TOut);
        }
    }
}