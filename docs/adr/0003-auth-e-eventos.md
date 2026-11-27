# ADR 0003 — Autoridades separadas e notificação pós-commit

A API é fronteira para integrações: valida access token por emissor e audience dedicados. O painel é a fronteira do operador: OIDC interativo, cookie e CSRF. A operação usa claims de tenant e transportadora, nunca uma seleção de organização do payload. Papéis de administração não atravessam tenants.

O formulário de pátio e o JSON de integração chamam os mesmos casos de uso. A identidade do usuário e a versão esperada acompanham a ação. A auditoria e o evento são persistidos junto com a mudança. O Worker não decide se uma reserva foi confirmada; processa trabalho que pode ocorrer depois, com Inbox persistente antes do ACK.

At-least-once é a garantia de transporte. Reordenar ou duplicar mensagens não equivale a reordenar ou duplicar o histórico da reserva. Projeções verificam a fonte de verdade atual quando a regra exige. Locks não cobrem I/O para RabbitMQ. O serviço local Keycloak utiliza identidades sintéticas, não configuração de produção.
