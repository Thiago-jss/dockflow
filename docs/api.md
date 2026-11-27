# API das transportadoras

Base local: `https://localhost:5191/api/v1/appointments`. O servidor exige access token Keycloak com audience `dockflow-api`, papel `Carrier` e claims `tenant_id`/`carrier_id` únicas. O cliente usa `client_credentials`; o token não deve ser colocado em URL, logs ou fixtures. A API não aceita a sessão de operador como autenticação.

| Método | Caminho | Resultado |
| --- | --- | --- |
| `GET` | `/api/v1/appointments` | Reservas visíveis à transportadora autenticada |
| `GET` | `/api/v1/appointments/{id}` | Detalhe, ou `404` para inexistente ou fora do escopo |
| `POST` | `/api/v1/appointments` | Agendamento `201` com `Location`, inclusive no replay idêntico |
| `POST` | `/api/v1/appointments/{id}/cancel` | Cancela com `{ "version": 1 }` usando a versão atual e estado permitido |

Para agendar, envie um único header `Idempotency-Key` (ASCII visível, máximo 128 caracteres, sem vírgula) e um JSON como:

```json
{
  "vehicle": "TRK-DEMO-01",
  "cargo": "Refrigerated",
  "weightKg": 12500,
  "start": "2027-04-01T14:00:00Z",
  "end": "2027-04-01T14:30:00Z"
}
```

`dockId` é opcional. O exemplo usa data ilustrativa, não uma reserva pré-carregada. As datas contêm offset; na persistência são normalizadas para minuto UTC. As categorias aceitas são `General` e `Refrigerated`. A chave de idempotência tem escopo de tenant, transportadora e sujeito. Reuso com conteúdo diferente devolve `409 idempotency_mismatch`. `reservation_overlap` significa choque de agenda; `dock_occupied` significa ocupação já existente no pátio, não necessariamente choque de janela. `dock_unavailable` cobre bloqueio ou incompatibilidade. Um cliente autenticado tem limite de 60 requisições/minuto por réplica neste laboratório.

O painel Razor usa fluxos distintos para portaria, operação e gestão; seus POSTs exigem token antiforgery. SignalR transmite apenas aviso de invalidação ao grupo do tenant, sem registros do negócio no evento do hub. Cada atualização relê a visão autorizada.
