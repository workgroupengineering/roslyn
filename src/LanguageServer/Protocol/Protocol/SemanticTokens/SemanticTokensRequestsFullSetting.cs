﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Client settings for semantic tokens related to the
    /// `textDocument/semanticTokens/full` message.
    ///
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#semanticTokensClientCapabilities">Language Server Protocol specification</see> for additional information.
    /// </summary>
    internal class SemanticTokensRequestsFullSetting
    {
        /// <summary>
        /// Gets or sets a value indicating whether the client will send the
        /// <c>textDocument/semanticTokens/full/delta</c> request if the server
        /// provides a corresponding handler.
        /// </summary>
        [JsonPropertyName("range")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Delta { get; set; }
    }
}
