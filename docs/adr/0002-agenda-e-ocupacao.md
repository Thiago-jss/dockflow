# ADR 0002 — Agenda e ocupação física são invariantes diferentes

Duas transportadoras podem ler o mesmo horário disponível. Um caminhão, por sua vez, pode continuar ocupando a doca depois do fim do horário contratado. Uma checagem só na aplicação ou uma chave Redis por horário não resolve ambos os casos.

O PostgreSQL aplica a exclusão por intervalo semiaberto; a ocupação tem restrição própria. O caso de uso faz a triagem de peso, categoria, agenda e bloqueio e devolve rejeição de domínio. A operação inicial não depende de notificação chegar nem de um processo de reconciliação rodar.

O custo é fixar o contrato de persistência em recursos nativos do PostgreSQL. Em troca, concorrência entre processos fica coberta pelo mesmo invariante, testado em banco real. Deixamos para uma fase futura o otimizador global de janelas flexíveis; o caso inicial usa intervalo explícito e seleção determinística.
