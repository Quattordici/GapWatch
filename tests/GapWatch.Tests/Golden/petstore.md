<!-- gapwatch-report -->
## GapWatch API compatibility report

❌ **Breaking changes detected** (20 breaking, 7 warning(s), 3 info)

`petstore-v1.json` → `petstore-v2.json`

### ❌ Breaking (20)

| Rule | Operation | Location | Details |
|---|---|---|---|
| GW044 | `DELETE /pets/{id}` | `response 204` | Success response 204 was removed. |
| GW052 | `DELETE /pets/{id}` | `security` | Previously accepted security (apiKey[]) was replaced by (bearer[]). |
| GW001 | `GET /owners` | `operation` | Operation 'GET /owners' was removed. |
| GW010 | `GET /pets` | `parameter 'region' (query)` | Required parameter 'region' (query) was added. |
| GW023 | `GET /pets` | `parameter 'status' (query)` | Enum value(s) no longer accepted: 'sold'. |
| GW020 | `GET /pets`<br>`GET /pets/{id}`<br>`POST /pets` | schema `Pet` `$.id` (response, 3 operations) | Type changed from 'integer' to 'string'. |
| GW029 | `GET /pets`<br>`GET /pets/{id}`<br>`POST /pets` | schema `Pet` `$.name` (response, 3 operations) | Response property 'name' is no longer guaranteed to be present. |
| GW022 | `GET /pets`<br>`GET /pets/{id}`<br>`POST /pets` | schema `Pet` `$.owner` (response, 3 operations) | The value can now be null. |
| GW052 | `GET /pets` | `security` | Previously accepted security (apiKey[]) was replaced by (bearer[]). |
| GW050 | `GET /pets/{id}` | `security` | The operation now requires authentication (bearer[]). |
| GW028 | `POST /pets` | `request body (application/json): $.kind` | Request property 'kind' became required. |
| GW027 | `POST /pets` | `request body (application/json): $.ownerId` | New required request property 'ownerId' was added. |
| GW043 | `POST /pets` | `request body (application/xml)` | Request media type 'application/xml' is no longer accepted. |
| GW052 | `POST /pets` | `security` | Previously accepted security (apiKey[]) was replaced by (bearer[]). |

### ⚠️ Warnings (7)

| Rule | Operation | Location | Details |
|---|---|---|---|
| GW024 | `GET /pets`<br>`GET /pets/{id}`<br>`POST /pets` | schema `Pet` `$.status` (response, 3 operations) | New enum value(s) can be returned: 'archived'. |
| GW026 | `GET /pets`<br>`GET /pets/{id}`<br>`POST /pets` | schema `Pet` `$.tag` (response, 3 operations) | Optional response property 'tag' was removed. |
| GW004 | `POST /pets` | `operation` | operationId changed from 'createPet' to 'addPet'. |

<details>
<summary>ℹ️ Info (3)</summary>

| Rule | Operation | Location | Details |
|---|---|---|---|
| GW002 | `DELETE /pets/{id}` | `operation` | Operation 'DELETE /pets/{id}' was marked deprecated. |
| GW003 | `GET /health` | `operation` | Operation 'GET /health' was added. |
| GW045 | `POST /pets` | `response 400` | Response 400 was removed. |

</details>
