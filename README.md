# FCG Catalog API

Microserviço .NET 10 da Fase 3 para catálogo, compra, biblioteca e avaliações. SQL Server mantém
jogos, pedidos, biblioteca e outbox; Redis atende o cache da listagem; MongoDB mantém avaliações;
RabbitMQ integra o processamento de pagamento.

## Eventos e transações

| Evento | Origem | Persistência de outbox |
|---|---|---|
| `OrderPlaced` | `POST /api/library/purchase` | Mesma transação do pedido `Pending` |
| `PaymentProcessed` | consumo do resultado do PaymentsAPI | Mesma transação da atualização do pedido/biblioteca |

O `OrderPlacedEvent` destinado ao PaymentsAPI é gravado pelo Bus Outbox do MassTransit no schema
`messaging`, dentro da mesma transação do pedido. O delivery service envia posteriormente ao
RabbitMQ. A notificação também não é publicada diretamente pela API: o `FCG-Outbox-Processor` lê
`dbo.OutboxMessages`, envia o payload integral à SQS e a `FCG-Notifications-Lambda` seleciona o
serviço correspondente por um `switch` sobre o enum derivado do texto de `EventType`.

O e-mail do usuário é obtido do JWT e armazenado no pedido para que o evento final tenha todos os
dados necessários sem chamada síncrona a outro serviço.

## Principais endpoints

| Método | Rota | Descrição |
|---|---|---|
| `GET/POST` | `/api/games` | Lista/cria jogos |
| `GET/PUT/DELETE` | `/api/games/{id}` | Consulta/atualiza/desativa jogo |
| `POST` | `/api/library/purchase` | Cria pedido e `OrderPlaced` |
| `GET` | `/api/library/{userId}` | Consulta biblioteca |
| `GET/POST` | `/api/games/{id}/reviews` | Avaliações em MongoDB |
| `GET` | `/health` | Health check |
| `GET` | `/metrics` | Métricas Prometheus |

## Banco e execução

O schema é responsabilidade da infraestrutura, incluindo `messaging.InboxState`,
`messaging.OutboxMessage` e `messaging.OutboxState`. O Compose aguarda `database-init`; em Kubernetes,
o `initContainer` aplica os scripts idempotentes antes da API. Para subir localmente:

```bash
cd FCG-Orchestration
docker compose up --build
```

A API fica em `http://localhost:5102` e as rotas públicas também passam pelo Kong em
`http://localhost:8000`.
