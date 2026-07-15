using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.ConnectorActions;

public static class ConnectorActionSchemaExporter
{
    public static string ExportV1()
    {
        var decisionPropertyName = JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorActionExecuteRequest.Decision));
        var approvalPropertyName = JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorActionExecuteRequest.Approval));

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = "https://openclaw.net/schemas/connector-action-execute-request.v1.json",
            ["title"] = nameof(ConnectorActionExecuteRequest),
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(
                JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorActionExecuteRequest.Proposal))),
                JsonValue.Create(decisionPropertyName)),
            ["properties"] = new JsonObject
            {
                [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorActionExecuteRequest.Proposal))] = new JsonObject
                {
                    ["type"] = "object"
                },
                [decisionPropertyName] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(
                        ConnectorActionContractCatalog.SupportedDecisions.Select(static decision => JsonValue.Create(decision)).ToArray())
                },
                [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorActionExecuteRequest.RiskLevel))] = new JsonObject
                {
                    ["type"] = "string"
                },
                [approvalPropertyName] = new JsonObject
                {
                    ["type"] = "object"
                }
            },
            ["allOf"] = new JsonArray(
                new JsonObject
                {
                    ["if"] = new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            [decisionPropertyName] = new JsonObject
                            {
                                ["const"] = ConnectorActionContractCatalog.RequireApproval
                            }
                        },
                        ["required"] = new JsonArray(JsonValue.Create(decisionPropertyName))
                    },
                    ["then"] = new JsonObject
                    {
                        ["required"] = new JsonArray(JsonValue.Create(approvalPropertyName)),
                        ["properties"] = new JsonObject
                        {
                            [approvalPropertyName] = new JsonObject
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["required"] = new JsonArray(
                                    JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.Approver))),
                                    JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.DecisionAt))),
                                    JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.DecisionReason))),
                                    JsonValue.Create(JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.TicketRef)))),
                                ["properties"] = new JsonObject
                                {
                                    [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.Approver))] = new JsonObject
                                    {
                                        ["type"] = "string"
                                    },
                                    [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.DecisionAt))] = new JsonObject
                                    {
                                        ["type"] = "string"
                                    },
                                    [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.DecisionReason))] = new JsonObject
                                    {
                                        ["type"] = "string"
                                    },
                                    [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.TicketRef))] = new JsonObject
                                    {
                                        ["type"] = "string"
                                    },
                                    [JsonNamingPolicy.CamelCase.ConvertName(nameof(ConnectorApprovalPayload.DecisionType))] = new JsonObject
                                    {
                                        ["type"] = "string"
                                    }
                                }
                            }
                        }
                    }
                })
        };

        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
