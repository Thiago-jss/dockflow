# Publicação e alvo de implantação

A demonstração do projeto é reproduzível em Docker Compose. O ambiente de laboratório não pode ser publicado como se fosse produção: a autenticação aceita explicitamente HTTP de loopback, o Keycloak inicia em modo dev, e PostgreSQL/RabbitMQ não têm redundância nessa topologia.

Para um deploy AWS, a fronteira recomendada é ALB HTTPS + ECS (Web e Worker como serviços separados) + RDS PostgreSQL + Amazon MQ para RabbitMQ **somente depois de verificar a compatibilidade do broker**, Redis gerenciado com TLS e um provedor OIDC de produção. O Worker atual usa RabbitMQ 4.3, quorum queues e dead-lettering at-least-once; a disponibilidade no serviço gerenciado precisa ser provada, não presumida. O receptor OTLP fica em rede privada.

A publicação de imagens deve promover o mesmo digest que foi testado. Para AWS, use uma role restrita com OIDC do GitHub Actions, escopo ao repositório e ambiente, revisão explícita antes de migração, provisionamento de secrets por Secrets Manager e verificação do digest na tarefa ativa. Não grave chaves de acesso AWS na pipeline. A aplicação e a migração usam credenciais distintas no banco.

Sem conta/role conectada, domínio/TLS e política de orçamento fornecidos, não há deploy remoto neste repositório. A etapa pública desta entrega é o código, os testes e a demonstração registrada, não um SaaS aberto a novos usuários.
