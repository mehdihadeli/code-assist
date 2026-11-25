export interface paths {
    "/v1/catalog/models": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListModels"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/catalog/models/{providerId}/{modelId}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetModel"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/catalog/providers": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListProviders"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/catalog/refresh": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RefreshCatalog"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/commands": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListConversationCommands"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/commands/expand": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ExpandConversationCommand"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/health": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["Health"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/management/{domain}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ManageRuntime"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/plugins/commands": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** List enabled, policy-permitted text contributions. */
        get: operations["ListPluginCommands"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/plugins/expand": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Expand explicitly selected text without model calls or tool execution. */
        post: operations["ExpandPluginPrompt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListSessions"];
        put?: never;
        post: operations["CreateSession"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/import": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ImportSession"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetSession"];
        put?: never;
        post?: never;
        delete: operations["DeleteSession"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/archive": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ExportSession"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/cancel": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["CancelTurn"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/checkpoints": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListSessionCheckpoints"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/connectors": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Inspect configured connectors for one existing conversation without opening connections. */
        get: operations["ListConnectors"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/connectors/{server}/connection": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** Inspect one session-owned connector. */
        get: operations["GetConnectorConnection"];
        put?: never;
        /** Explicitly start a trusted configured connector; may launch a child process or OAuth flow. */
        post: operations["CreateConnectorConnection"];
        /** Close this conversation's connector without removing persisted credentials. */
        delete: operations["DeleteConnectorConnection"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/connectors/{server}/credentials": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post?: never;
        /** Close this session's connector and remove locally owned credentials; does not revoke them at the issuer. */
        delete: operations["ForgetConnectorCredentials"];
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/connectors/{server}/prompts": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** List untrusted prompt metadata from an already connected server. */
        get: operations["ListConnectorPrompts"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/connectors/{server}/prompts/resolve": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Retrieve untrusted prompt messages without promoting roles, invoking tools or calling a model. */
        post: operations["ResolveConnectorPrompt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/details": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["GetSessionDetails"];
        put?: never;
        post: operations["UpdateSessionDetails"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/fork": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["ForkSession"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/messages": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get: operations["ListMessages"];
        put?: never;
        post: operations["SendMessage"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/messages/stream": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["StreamMessage"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/sessions/{id}/rewind": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["RewindSession"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/skills/commands": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        /** List policy-admitted human skill guidance without reading bodies or calling models. */
        get: operations["ListSkillCommands"];
        put?: never;
        post?: never;
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/skills/expand": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        /** Expand explicit human guidance without gathering context, starting children or calling models. */
        post: operations["ExpandSkillPrompt"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
    "/v1/tools/query": {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        get?: never;
        put?: never;
        post: operations["QueryTools"];
        delete?: never;
        options?: never;
        head?: never;
        patch?: never;
        trace?: never;
    };
}
export type webhooks = Record<string, never>;
export interface components {
    schemas: {
        AgentMessageDto: {
            /** Format: date-time */
            createdAt: string;
            role: string;
            /** Format: int32 */
            sequence: number | string;
            text: string;
        };
        AgentSessionArchive: {
            /** Format: date-time */
            createdAt: string;
            messages: components["schemas"]["AgentMessageDto"][];
            model: string;
            state: components["schemas"]["JsonElement"];
            /** Format: int32 */
            version: number | string;
        };
        AgentSessionDetails: {
            archived: boolean;
            /** Format: int64 */
            completedTurns: number | string;
            /** Format: int64 */
            failedTurns: number | string;
            /** Format: uuid */
            id: string;
            /** Format: int64 */
            inputTokens: number | string;
            /** Format: int64 */
            outputTokens: number | string;
            /** Format: int64 */
            revision: number | string;
            title: string;
            tokensReported: boolean;
        };
        AgentSessionDto: {
            /** Format: date-time */
            createdAt: string;
            /** Format: uuid */
            id: string;
            /** Format: int32 */
            messageCount: number | string;
            model: string;
            running: boolean;
        };
        AgentTurnEvent: {
            kind: string;
            /** Format: int32 */
            sequence: number | string;
            text: null | string;
            /** Format: uuid */
            turnId: string;
        };
        CancelAgentTurnResponse: {
            canceled: boolean;
        };
        CatalogModelDto: {
            attachment?: null | boolean;
            /** Format: int32 */
            contextWindow: null | number | string;
            input: string[];
            /** Format: int32 */
            inputTokenLimit?: null | number | string;
            /** Format: int32 */
            maxOutputTokens: null | number | string;
            modelId: string;
            name: string;
            output: string[];
            price: null | components["schemas"]["CatalogPriceDto"];
            providerId: string;
            reasoning: boolean;
            reasoningEfforts?: string[];
            status: null | string;
            temperature?: null | boolean;
            toolCallSupport?: null | boolean;
            toolCalls: boolean;
        };
        CatalogPriceDto: {
            /** Format: double */
            cacheRead: null | number | string;
            /** Format: double */
            cacheWrite: null | number | string;
            /** Format: double */
            input: null | number | string;
            /** Format: double */
            output: null | number | string;
            tiers?: components["schemas"]["CatalogPriceTierDto"][];
        };
        CatalogPriceTierDto: {
            /** Format: int32 */
            aboveTokens: number | string;
            price: components["schemas"]["CatalogPriceDto"];
        };
        CatalogProviderDto: {
            api: null | string;
            doc: null | string;
            env: string[];
            id: string;
            name: string;
            protocol: null | string;
        };
        CatalogRefreshDto: {
            error: null | string;
            /** Format: int32 */
            modelCount: number | string;
            status: string;
        };
        ConversationCommandDto: {
            description: string;
            hint?: null | string;
            kind: string;
            name: string;
            origin: string;
        };
        ConversationCommandExpansion: {
            model: null | string;
            role: null | string;
            text: string;
        };
        CreateAgentSessionRequest: {
            model?: null | string;
        };
        CreateConnectorConnectionRequest: {
            /** @default false */
            confirmed: boolean;
        };
        ExpandConversationCommandRequest: {
            input: string;
            name: string;
            values?: null | {
                [key: string]: string;
            };
        };
        ExpandPluginRequest: {
            /** @default  */
            input: string;
            name: string;
        };
        ExpandSkillRequest: {
            arguments?: null | string[];
            id: string;
            /** @default  */
            input: string;
        };
        JsonElement: unknown;
        ManagementCommandRequest: {
            arguments: components["schemas"]["JsonElement"];
            operation: string;
        };
        ManagementReply: {
            /**
             * Format: int32
             * @default 0
             */
            exitCode: number | string;
            /** @default true */
            hasValue: boolean;
            summary: string;
            value: components["schemas"]["JsonElement"];
        };
        McpConnectionStatus: {
            connected: boolean;
            server: string;
            /** Format: int32 */
            tools: number | string;
        };
        McpPromptArgument: {
            description: null | string;
            name: string;
            required: boolean;
        };
        McpPromptEntry: {
            arguments: components["schemas"]["McpPromptArgument"][];
            description: null | string;
            name: string;
        };
        McpPromptMessage: {
            content: components["schemas"]["ToolResult"];
            role: string;
        };
        McpPromptResult: {
            description: null | string;
            messages: components["schemas"]["McpPromptMessage"][];
        };
        PluginCommandDto: {
            /** Format: int32 */
            contentBytes: number | string;
            description: string;
            /** Format: int32 */
            descriptionBytes: number | string;
            kind: string;
            name: string;
        };
        PluginPromptDto: {
            text: string;
        };
        ResolveConnectorPromptRequest: {
            arguments?: null | {
                [key: string]: string;
            };
            name: string;
        };
        RewindSessionRequest: {
            /** Format: int64 */
            expectedRevision: number | string;
            /** Format: int64 */
            revision: number | string;
        };
        SendAgentMessageRequest: {
            text: string;
        };
        ServerHealthDto: {
            application: string;
            protocol: string;
            status: string;
        };
        SkillCommandDto: {
            description: string;
            hint: null | string;
            id: string;
            origin: string;
            parameters: string[];
            title: null | string;
        };
        SkillPromptDto: {
            text: string;
        };
        ToolAttachment: {
            /** Format: byte */
            data: string;
            mimeType: string;
        };
        ToolInfoDto: {
            description: string;
            inputSchema: string;
            name: string;
            readOnly: boolean;
            risk: string;
            tags: string[];
        };
        ToolQueryRequest: {
            allow: null | string[];
            deny: null | string[];
        };
        ToolResult: {
            attachments?: null | components["schemas"]["ToolAttachment"][];
            /** @default false */
            isError: boolean;
            /** Format: uuid */
            outputId?: null | string;
            structuredContent?: unknown;
            text: string;
        };
        UpdateSessionRequest: {
            archived: boolean;
            title: string;
        };
    };
    responses: never;
    parameters: never;
    requestBodies: never;
    headers: never;
    pathItems: never;
}
export type $defs = Record<string, never>;
export interface operations {
    ListModels: {
        parameters: {
            query?: {
                providerId?: string;
            };
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogModelDto"][];
                };
            };
        };
    };
    GetModel: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                providerId: string;
                modelId: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogModelDto"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ListProviders: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogProviderDto"][];
                };
            };
        };
    };
    RefreshCatalog: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CatalogRefreshDto"];
                };
            };
        };
    };
    ListConversationCommands: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConversationCommandDto"][];
                };
            };
        };
    };
    ExpandConversationCommand: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ExpandConversationCommandRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ConversationCommandExpansion"];
                };
            };
        };
    };
    Health: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ServerHealthDto"];
                };
            };
        };
    };
    ManageRuntime: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                domain: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ManagementCommandRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ManagementReply"];
                };
            };
        };
    };
    ListPluginCommands: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PluginCommandDto"][];
                };
            };
        };
    };
    ExpandPluginPrompt: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ExpandPluginRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["PluginPromptDto"];
                };
            };
            /** @description Bad Request */
            400: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ListSessions: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDto"][];
                };
            };
        };
    };
    CreateSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateAgentSessionRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDto"];
                };
            };
        };
    };
    ImportSession: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["AgentSessionArchive"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDto"];
                };
            };
        };
    };
    GetSession: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDto"];
                };
            };
        };
    };
    DeleteSession: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ExportSession: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionArchive"];
                };
            };
        };
    };
    CancelTurn: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["CancelAgentTurnResponse"];
                };
            };
        };
    };
    ListSessionCheckpoints: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": (number | string)[];
                };
            };
        };
    };
    ListConnectors: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["McpConnectionStatus"][];
                };
            };
        };
    };
    GetConnectorConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                server: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["McpConnectionStatus"];
                };
            };
        };
    };
    CreateConnectorConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                server: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["CreateConnectorConnectionRequest"];
            };
        };
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["McpConnectionStatus"];
                };
            };
        };
    };
    DeleteConnectorConnection: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                server: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ForgetConnectorCredentials: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                server: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description No Content */
            204: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    ListConnectorPrompts: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                server: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["McpPromptEntry"][];
                };
            };
        };
    };
    ResolveConnectorPrompt: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
                server: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ResolveConnectorPromptRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["McpPromptResult"];
                };
            };
        };
    };
    GetSessionDetails: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDetails"];
                };
            };
        };
    };
    UpdateSessionDetails: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["UpdateSessionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDetails"];
                };
            };
        };
    };
    ForkSession: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description Created */
            201: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDto"];
                };
            };
        };
    };
    ListMessages: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentMessageDto"][];
                };
            };
        };
    };
    SendMessage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SendAgentMessageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentMessageDto"];
                };
            };
        };
    };
    StreamMessage: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["SendAgentMessageRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "text/event-stream": components["schemas"]["AgentTurnEvent"];
                };
            };
        };
    };
    RewindSession: {
        parameters: {
            query?: never;
            header?: never;
            path: {
                id: string;
            };
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["RewindSessionRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["AgentSessionDto"];
                };
            };
        };
    };
    ListSkillCommands: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody?: never;
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SkillCommandDto"][];
                };
            };
        };
    };
    ExpandSkillPrompt: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ExpandSkillRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["SkillPromptDto"];
                };
            };
            /** @description Not Found */
            404: {
                headers: {
                    [name: string]: unknown;
                };
                content?: never;
            };
        };
    };
    QueryTools: {
        parameters: {
            query?: never;
            header?: never;
            path?: never;
            cookie?: never;
        };
        requestBody: {
            content: {
                "application/json": components["schemas"]["ToolQueryRequest"];
            };
        };
        responses: {
            /** @description OK */
            200: {
                headers: {
                    [name: string]: unknown;
                };
                content: {
                    "application/json": components["schemas"]["ToolInfoDto"][];
                };
            };
        };
    };
}
