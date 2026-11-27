# Evidência de engenharia

Os cenários abaixo foram executados em um laboratório local isolado, com identidades e veículos fictícios, antes da publicação do código. Nenhum teste foi dirigido a um ambiente de terceiros.

| Cenário | Evidência | Reproduzir |
| --- | --- | --- |
| Concorrência na mesma doca | Execuções HTTP, incluindo após o redesign: 1 resposta `201`, 99 respostas `409`; intervalo adjacente aceito | `python3 scripts/lab.py api` |
| Isolamento | Transportadora par do mesmo tenant e transportadora de outro tenant não leem a reserva vencedora | Mesmo teste da API |
| Banco real | 8 testes xUnit passaram, com migrações e PostgreSQL descartável; build Release sem erros nem avisos | `dotnet test tests/DockFlow.CoreTests -c Release` |
| Recuperação de broker | Reserva, ledger de auditoria e outbox verificadas com RabbitMQ parado; o mesmo evento apareceu na inbox após o retorno | `python3 scripts/broker_lab.py --project dockflow-qa` |
| Entrega e DLQ | Dez reentregas via RabbitMQ sem novo efeito; mensagens inválidas e retentativas esgotadas na DLQ | `bash tests/DockFlow.WorkerTests/run-integration.sh` |
| Operação no navegador | OIDC local, cookie, POST sem CSRF rejeitado, quatro transições e evento SSE/SignalR recebido. Redesign verificado com mudança de data, preservação da data após POST, estado de ocupação e viewport de 390 px sem overflow da página | `python3 scripts/lab.py browser --vehicle <veículo> --day YYYY-MM-DD` |
| Redis indisponível | Reserva confirmada; indicador de descargas do painel correspondeu ao PostgreSQL | `python3 scripts/cache_lab.py --project dockflow-qa` |

Os comandos de falha requerem o projeto Compose de nome `dockflow-qa` e manipulam apenas esse laboratório. O cenário do cache precisa de pelo menos uma descarga concluída no dia. Para preparar Chromium, consulte [testes E2E](../tests/e2e/README.md).

## Desempenho em laboratório

- **API**: k6 1.5.0, aproximadamente 1 leitura autenticada por segundo por 30 segundos. Foram **31 iterações**, sem falhas ou drops; **p95 13,08 ms, p99 14,3 ms** no subgrupo do endpoint. O teste não mede capacidade máxima, cauda de produção ou desempenho sob escrita simultânea.
- **SQL**: `tests/load/analytics-plan.sql` usa uma tabela temporária de 250 mil linhas sintéticas. O recorte retornou 7 linhas antes/depois; o plano mudou de `Seq Scan` (**11,146 ms**) para `Bitmap Index Scan` com leitura no heap (**0,063 ms**). É uma medição do plano nesse conjunto, não um SLA de latência do painel.
- **Observabilidade**: séries reais de HTTP e cache foram consultadas no Prometheus, e o painel provisionado foi aberto no Grafana. Não há comprovação de tracing distribuído entre Web e Worker.

Os logs completos que contêm caminhos locais e os dados gerados pelo laboratório não acompanham o código público.
