# FCG Catalog API - Fase 4

Microserviço .NET 10 para catálogo, compra, biblioteca e avaliações, evoluindo a base da Fase 3
para a Fase 4. SQL Server mantém
jogos, pedidos, biblioteca e outbox; Redis atende o cache da listagem; MongoDB mantém avaliações;
RabbitMQ integra o processamento de pagamento.

## Situação da Fase 4

MongoDB, Redis e busca Elasticsearch estão integrados na `master`. A API expõe `/search`,
`/api/games/search` e reindexação administrativa. A busca usa fuzzy `AUTO`, título com peso maior
e ordenação por score; criação, edição e exclusão lógica atualizam o índice.
O motor dos manifests roda dentro do cluster: falta atender à exigência específica de
Elasticsearch/OpenSearch em serviço gerenciado ou confirmar essa alternativa com a disciplina.

O [workflow](.github/workflows/publish-container.yml) testa o serviço, autentica por OIDC,
publica no Amazon ECR com tags SHA e `latest`, executa Trivy não bloqueante e atualiza o
Deployment no EKS, aguardando o rollout. PRs executam somente testes. Configuração em
[CI/CD](https://github.com/posgraduacaofiapnet/FCG-Orchestration/blob/master/docs/CICD.md).
O registro cloud de 11/11/2026 é histórico; esta revisão documental de 16/11/2026 não repete
deploys nem testes no ambiente ao vivo. Credenciais fixas remanescentes, ganho de cache e
continuidade de tráfego durante rollout ainda precisam ser tratados nas evidências finais.
Consultar a [matriz da Fase 4](https://github.com/posgraduacaofiapnet/FCG-Orchestration/blob/master/docs/FASE4.md)
e a [arquitetura central](https://github.com/posgraduacaofiapnet/FCG-Orchestration/blob/master/docs/ARQUITETURA.md).

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

## Stack

- .NET 10 / ASP.NET Core
- Entity Framework Core 10 + SQL Server
- MassTransit + RabbitMQ
- Redis e MongoDB
- JWT Bearer Authentication
- Swagger / OpenAPI
- Serilog (logs estruturados em JSON)
- Prometheus-net (Métricas de aplicação)

## Principais endpoints

| Método | Rota | Descrição |
|---|---|---|
| `GET/POST` | `/api/games` | Lista pública paginada; criação exige JWT com role `Admin` |
| `GET/PUT/DELETE` | `/api/games/{id}` | Consulta pública; atualização/desativação exigem `Admin` |
| `POST` | `/api/library/purchase` | Cria pedido e `OrderPlaced`; JWT e dono do `userId` obrigatórios |
| `GET` | `/api/library/{userId}` | Biblioteca do próprio usuário autenticado |
| `GET/POST` | `/api/games/{id}/reviews` | Consulta pública; criação autenticada, autor obtido do JWT |
| `GET` | `/search` ou `/api/games/search` | Busca pública com `q` e `limit`, fuzzy search e score |
| `POST` | `/api/games/reindex` | Reindexa jogos ativos; exige role `Admin` |
| `GET` | `/api/notifications/status?limit=20` | Cruza os outboxes recentes com o status gravado pela Lambda no DynamoDB (Admin) |
| `GET` | `/api/notifications/status/{eventId}` | Consulta uma notificação específica no outbox e DynamoDB (Admin) |
| `GET` | `/health` | Health check |
| `GET` | `/metrics` | Métricas Prometheus |

## Banco e execução

O schema é responsabilidade da infraestrutura, incluindo `messaging.InboxState`,
`messaging.OutboxMessage` e `messaging.OutboxState`. O Compose aguarda `database-init`; em Kubernetes,
o `initContainer` aplica os scripts idempotentes antes da API. Para subir localmente:

```bash
cd FCG-Orchestration
powershell -NoProfile -ExecutionPolicy Bypass -File ./start-local.ps1
```

A API fica em `http://localhost:5102` e as rotas públicas também passam pelo Kong em
`http://localhost:8000`.

O comando usa PowerShell e exige os repositórios irmãos UsersAPI, PaymentsAPI,
`FCG-Outbox-Processor` e `FCG-Notifications-Lambda`. O script gera o pacote da Lambda antes de
subir LocalStack. Ver [pré-requisitos e validação](https://github.com/posgraduacaofiapnet/FCG-Orchestration/blob/master/docs/VALIDACAO.md).

Os endpoints de status não fazem varredura no DynamoDB. A API primeiro seleciona IDs existentes no
outbox local do Catalog e consulta somente essas chaves. `lambdaProcessed: true` significa que a
Lambda gravou `Status=Completed` na tabela `fcg-notification-idempotency`.

## MongoDB e avaliações

`MongoDB.Driver` persiste a collection `reviews`, com índice por `gameId`. A API verifica que o
jogo existe e está ativo no SQL, recebe `rating` de 1 a 5 e `comment` não vazio de até 500
caracteres, e associa a avaliação ao `user_id` do token. A listagem retorna as mais recentes primeiro.
SQL mantém jogos/pedidos/biblioteca; MongoDB mantém os documentos de avaliação.

Exemplo de corpo para `POST /api/games/{id}/reviews`, com token de usuário:

```json
{
  "rating": 5,
  "comment": "Ótima experiência de jogo."
}
```

## Cache do catálogo

`GET /api/games?page=1&pageSize=10` usa Redis via `IDistributedCache`, com prefixo `fcg:`,
chaves por versão/página/tamanho e TTL de 5 minutos. Criar, editar ou desativar jogo invalida
a listagem por troca de versão. Há logs de hit/miss e fallback ao SQL em falha do cache.
Sem conexão Redis configurada, o fallback é memória local do processo, que não comprova
cache distribuído. O ganho de latência ainda precisa ser medido no ambiente final.

## Configuração

| Variável | Finalidade |
|---|---|
| `ConnectionStrings__DefaultConnection` | SQL Server `FCGCatalogDb` |
| `ConnectionStrings__MongoDB` / `Mongo__Database` | Conexão e banco MongoDB |
| `ConnectionStrings__Redis` | Conexão do cache distribuído |
| `ConnectionStrings__Elasticsearch` / `Elasticsearch__Url` | Endereço do motor de busca; connection string tem prioridade |
| `Elasticsearch__Index` | Índice, padrão `fcg-games` |
| `Elasticsearch__ApiKey` | Autenticação opcional, com prioridade sobre usuário/senha |
| `Elasticsearch__Username` / `Elasticsearch__Password` | Autenticação alternativa do motor |
| `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience` | Validação coerente com UsersAPI e Kong |
| `RabbitMq__Host`, `RabbitMq__Username`, `RabbitMq__Password` | Broker de pagamentos |
| `RabbitMq__PaymentProcessedQueue` | Fila de resultados, padrão `catalog-payment-processed` |
| `Notifications__DynamoDbTableName` | Tabela de status/idempotência de notificações |
| `Notifications__DynamoDbServiceUrl` | Endpoint local opcional; vazio para AWS real |
| `AWS_REGION` | Região do cliente DynamoDB; autenticação conforme ambiente |

Os manifests agora versionam `k8s/secret.example.yaml`; `k8s/secret.yaml` é local e ignorado.
Preparar os valores reais e aplicar pelos scripts do Orchestration. Não aplicar a pasta `k8s/`
inteira, pois o exemplo poderia sobrescrever Secrets. A remoção dos antigos Secrets versionados
não elimina valores fixos remanescentes em appsettings, Compose e seed. O manifesto MongoDB
atual não define PVC.

## Busca e consistência do índice

Exemplo: `GET /search?q=Cyber&limit=10`. Limite padrão 20, máximo 100; consulta vazia retorna
lista vazia. `POST /api/games/reindex` requer token Admin. Na inicialização há tentativa de
reindexar os jogos ativos. Falhas de indexação são logadas sem desfazer a gravação SQL; o índice
pode ficar desatualizado até uma reindexação bem-sucedida. Score e retorno de uma consulta exata
não comprovam tolerância a erros: testar também uma consulta com erro de digitação.

## Testes e limites da validação

Na raiz deste repositório:

```bash
dotnet test tests/CatalogAPI.UnitTests/CatalogAPI.UnitTests.csproj --configuration Release
```

Registro histórico de 10/11/2026, anterior à integração da busca: 36 aprovados e 1 ignorado.
Não é a contagem da suíte atual, que ganhou testes de busca. O teste de integração MongoDB depende de
`FCG_TEST_MONGO_CONNECTION`; não foi executado contra banco real nessa rodada. Os testes de cache
utilizam doubles/memória e não substituem teste com Redis real nem benchmark. O restore reportou
NU1902/NU1903 para `System.Security.Cryptography.Pkcs` 9.0.4. Stack completa e nuvem não foram
executadas nessa revisão. A pipeline atual não configura conexão de teste MongoDB.
