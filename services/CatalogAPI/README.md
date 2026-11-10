# CatalogAPI

Microsservico responsavel pelo catalogo de jogos, inicio do fluxo de compra e atualizacao da biblioteca apos pagamento aprovado.

## Endpoints

- `GET /api/games`
- `GET /api/games/{id}`
- `POST /api/games` (JWT com role `Admin`)
- `PUT /api/games/{id}` (JWT com role `Admin`)
- `DELETE /api/games/{id}` (JWT com role `Admin`, soft delete via IsActive)
- `POST /api/library/purchase` (JWT obrigatorio, `userId` do corpo deve ser o dono do token)
- `GET /api/library/{userId}` (JWT obrigatorio, so o proprio dono)
- `POST /api/games/{id}/reviews` (JWT obrigatorio; autor = `user_id` do token; MongoDB)
- `GET /api/games/{id}/reviews`
- `GET /health`
- `GET /metrics`
- `GET /api/notifications/status` (JWT com role `Admin`)
- `GET /api/notifications/status/{eventId}` (JWT com role `Admin`)

## Eventos

- Publica: `OrderPlacedEvent`
- Consome: `PaymentProcessedEvent`

## Variaveis

- `ConnectionStrings__DefaultConnection`
- `Jwt__Key` (mesmo valor do FCG-UsersAPI)
- `Jwt__Issuer` (mesmo valor do FCG-UsersAPI)
- `Jwt__Audience` (mesmo valor do FCG-UsersAPI)
- `RabbitMq__Host`
- `RabbitMq__Username`
- `RabbitMq__Password`
- `RabbitMq__PaymentProcessedQueue`
- `ConnectionStrings__Redis`
- `ConnectionStrings__MongoDB`
- `Mongo__Database`
- `Notifications__DynamoDbTableName`
- `Notifications__DynamoDbServiceUrl`
- `AWS_REGION`

Este projeto já pertence ao repositório separado `FCG-CatalogAPI`. O [README principal](../../README.md)
documenta a Fase 4, configurações, autorização, testes, cache Redis e avaliações MongoDB.
A busca está na branch `feature/elasticsearch`, fora da `master`; não há `/search` nesta revisão
da branch principal. Notificações atuais usam outbox SQL/SQS/Lambda, separadamente do fluxo de
pagamento no RabbitMQ.
