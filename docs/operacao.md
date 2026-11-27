# Operação local

O Compose de laboratório usa serviços isolados e expõe apenas o painel em `https://localhost:5191` e o Keycloak local em `http://localhost:5192`. Banco, Redis, RabbitMQ e a administração do proxy não são publicados. Não use esta configuração na internet: Keycloak em `start-dev`, HTTP de identidade no loopback, certificado de laboratório e usuários sintéticos não equivalem a configuração produtiva.

```sh
python3 scripts/bootstrap.py
docker compose up -d --build
python3 scripts/check_identity.py
python3 scripts/wait_ready.py
```

`bootstrap.py` exige Python 3 e `openssl`. Ele grava segredos aleatórios no `.env` com modo `0600`; a chave TLS e as configurações do laboratório ficam em `.local/` com modo `0700` no diretório. O script não mostra valores nem troca os segredos em uma segunda execução. Consulte localmente `DOCKFLOW_OPERATOR_PASSWORD` no seu gerenciador/editor de arquivos e use a conta fictícia `manager-a`. Não cole o valor no chat nem o serialize em artefatos. Para testes via linha de comando, somente `.local/tls.crt` é necessário como âncora; **não instale o certificado globalmente**.

O serviço `migrate` termina após aplicar migrations e, só no laboratório, semear duas organizações. A aplicação usa um usuário do banco sem permissão de DDL; o serviço de migração tem credencial separada. `docker compose stop` preserva o estado. Use `docker compose down` para remover contêineres e rede, sem apagar volumes. Gere outro laboratório em vez de apagar uma base que não identificou.

## Observabilidade opcional

```sh
docker compose -f compose.yaml -f compose.observability.yaml up -d --build
```

O painel Grafana de laboratório está em <http://localhost:5193/d/dockflow-ops/> e Prometheus em <http://localhost:5194/>. O Collector recebe OTLP em rede interna; exporta métricas ao Prometheus e escreve apenas o resumo de spans no log. O painel mostra taxa, latência de requisições e leituras do cache. O span de indicadores é local ao Web; não há propagação de contexto ao Worker nem armazenamento de traces de produção. Nunca exponha o Grafana de laboratório com acesso anônimo fora do loopback.

## Falhas esperadas

| Sintoma | Verificação |
| --- | --- |
| Sem login ou API devolve `401` | Rode `python3 scripts/check_identity.py`; valida emissor e emissão de claims para os três clientes fictícios. O servidor também valida assinatura, audiência e validade. |
| `409` ao reservar | Conflito de intervalo, versão ou reutilização indevida da chave de operação. A definição do conflito vem do estado atual do banco, não de Redis. |
| Broker fora do ar | O agendamento e a outbox permanecem duráveis. Verifique o Worker e o crescimento da outbox antes de reenviar manualmente. |
| Doca bloqueada | Consulte os incidentes e realoque as visitas que ainda não ocuparam fisicamente a doca. |

## Corte para ambiente real

Antes de expor o serviço: substitua o IdP de laboratório por um tenant produtivo com HTTPS, configure audience e emissor explícitos, gerencie secrets fora do Compose e dê escopos distintos aos runners. Separe DB/migrations de runtime, persistência de chaves de sessão, backups, telemetria, IAM e a política de retenção de auditoria. Um build ou repositório público não comprova deploy em nuvem.
