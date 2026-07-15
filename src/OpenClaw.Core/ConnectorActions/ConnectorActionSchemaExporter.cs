namespace OpenClaw.Core.ConnectorActions;

public static class ConnectorActionSchemaExporter
{
    public static string ExportV1()
        => """
           {
             "$schema": "https://json-schema.org/draft/2020-12/schema",
             "$id": "https://openclaw.net/schemas/connector-action-execute-request.v1.json",
             "title": "ConnectorActionExecuteRequest",
             "type": "object",
             "additionalProperties": false,
             "required": [
               "proposal",
               "decision"
             ],
             "properties": {
               "proposal": {
                 "type": "object"
               },
               "decision": {
                 "type": "string",
                 "enum": [
                   "proceed",
                   "require_approval",
                   "reject",
                   "escalate"
                 ]
               },
               "riskLevel": {
                 "type": "string"
               },
               "approval": {
                 "type": "object",
                 "additionalProperties": false,
                 "required": [
                   "approver",
                   "decisionAt",
                   "decisionReason",
                   "ticketRef"
                 ],
                 "properties": {
                   "approver": {
                     "type": "string"
                   },
                   "decisionAt": {
                     "type": "string"
                   },
                   "decisionReason": {
                     "type": "string"
                   },
                   "ticketRef": {
                     "type": "string"
                   },
                   "decisionType": {
                     "type": "string"
                   }
                 }
               }
             }
           }
           """;
}
