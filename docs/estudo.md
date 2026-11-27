# Como estudar os pontos decisivos

1. **Concorrência:** siga `BookAsync` em `PostgresDockOperations.cs` até a constraint `visits_no_overlapping_reservations`. Mude a janela do exercício para começar exatamente no fim da anterior; explique por que `[início, fim)` permite isso. Compare o teste xUnit com o disparo real via API.
2. **Agenda versus presença:** siga `VisitLifecycle.Apply` e a restrição parcial `visits_one_physical_occupant`. Explique por que nunca se deve usar o relógio para promover uma operação em andamento para concluída.
3. **Entrega e falha:** acompanhe `OutboxRelay` e `IncidentConsumer` no Worker. Pare o broker no laboratório com `scripts/broker_lab.py`; compare a persistência antes e depois. Explique a janela entre confirmação do broker e atualização da outbox.
4. **Autoridade:** trace `Identity.ActorFor` até o escopo nas consultas PostgreSQL. Explique a diferença entre o token da transportadora e o cookie de um operador, e por que conhecer o ID de outra reserva não dá acesso.

Prática sugerida: escreva primeiro um teste de realocação com doca simultaneamente bloqueada, depois implemente ou ajuste a regra sem remover a exclusão do banco. Repita o exercício com uma manhã de operação e tempos de chegada antecipada e tardia, documentando a política que escolheu.
