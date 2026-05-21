using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;

namespace RPG.Quest
{
    /// <summary>
    /// Gerenciador de quests do jogador. Server-authoritative; o cliente
    /// apenas lê a SyncList e exibe progresso.
    ///
    /// === ANEXAR ONDE? ===
    ///   Adicione este componente ao playerPrefab (mesmo GameObject de
    ///   NetworkPlayer / NetworkInventory).
    ///
    /// === FLUXO ===
    ///   1. Cliente clica num NPC com quest disponível.
    ///   2. NetworkNPC valida e chama QuestManager.ServerTryOfferQuest.
    ///   3. Player aceita via DialogUI → CmdAcceptQuest → ServerAcceptQuest.
    ///   4. Eventos do mundo (matar mob, pegar item) chamam NotifyEvent.
    ///   5. Ao completar todos objetivos, estado → ReadyToTurnIn.
    ///   6. Player retorna ao NPC; NetworkNPC chama ServerCompleteQuest.
    ///
    /// === PERSISTÊNCIA ===
    ///   ServerLoadFromDatabase é chamado em NetworkPlayer.ServerInitialize.
    ///   ServerSaveAll é chamado em conjunto com inventário (auto-save + logout).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class QuestManager : NetworkBehaviour
    {
        public const int MAX_ACTIVE_QUESTS = 25;

        // Cap defensivo: nenhum jogador realista terá mais que isso completo.
        // 10000 quests completadas = ~500KB no banco. Hard cap.
        public const int MAX_COMPLETED_QUESTS_TRACKED = 10000;

        /// <summary>
        /// Lista sincronizada de TODAS as quests que o jogador já tocou
        /// (ativas, prontas, completadas). Reorganizada no servidor.
        /// </summary>
        public readonly SyncList<QuestProgress> Quests = new SyncList<QuestProgress>();

        // ── Eventos no cliente ─────────────────────────────────────────────
        public event Action                        OnQuestsChanged;
        public event Action<string>                OnQuestAccepted;
        public event Action<string>                OnQuestCompleted;
        public event Action<string, int, int, int> OnObjectiveProgress; // questId, objIndex, current, target
        public event Action<string>                OnQuestReadyToTurnIn;

        // ── Componentes ────────────────────────────────────────────────────
        private RPG.Network.NetworkPlayer    _netPlayer;
        private RPG.Network.NetworkInventory _inventory;

        // No servidor: cache do CharacterId/Username para persistência
        private string _serverCharacterId;
        private string _serverAccountUsername;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _netPlayer = GetComponent<RPG.Network.NetworkPlayer>();
            _inventory = GetComponent<RPG.Network.NetworkInventory>();
        }

        public override void OnStartClient()
        {
            Quests.Callback += OnQuestsSyncCallback;
        }

        public override void OnStopClient()
        {
            Quests.Callback -= OnQuestsSyncCallback;
        }

        private void OnQuestsSyncCallback(SyncList<QuestProgress>.Operation op,
                                          int index,
                                          QuestProgress oldItem,
                                          QuestProgress newItem)
        {
            OnQuestsChanged?.Invoke();

            switch (op)
            {
                case SyncList<QuestProgress>.Operation.OP_ADD:
                    if (newItem.State == QuestState.Active)
                        OnQuestAccepted?.Invoke(newItem.QuestId);
                    break;

                case SyncList<QuestProgress>.Operation.OP_SET:
                    if (oldItem.State != newItem.State)
                    {
                        if (newItem.State == QuestState.ReadyToTurnIn)
                            OnQuestReadyToTurnIn?.Invoke(newItem.QuestId);
                        else if (newItem.State == QuestState.Completed)
                            OnQuestCompleted?.Invoke(newItem.QuestId);
                    }
                    else if (oldItem.ProgressCsv != newItem.ProgressCsv)
                    {
                        DiffAndNotifyProgress(oldItem, newItem);
                    }
                    break;
            }
        }

        private void DiffAndNotifyProgress(QuestProgress oldItem, QuestProgress newItem)
        {
            var def = QuestDatabase.Instance?.GetQuest(newItem.QuestId);
            if (def == null) return;

            int count = def.ObjectiveCount;
            var oldArr = oldItem.GetProgressArray(count);
            var newArr = newItem.GetProgressArray(count);

            for (int i = 0; i < count; i++)
            {
                if (oldArr[i] != newArr[i])
                {
                    int target = def.GetObjective(i)?.TargetCount ?? 0;
                    OnObjectiveProgress?.Invoke(newItem.QuestId, i, newArr[i], target);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública — LEITURA (segura em ambos os lados)
        // ══════════════════════════════════════════════════════════════════

        public int FindIndexById(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return -1;
            for (int i = 0; i < Quests.Count; i++)
                if (Quests[i].QuestId == questId) return i;
            return -1;
        }

        public QuestProgress? FindByIdNullable(string questId)
        {
            int idx = FindIndexById(questId);
            return idx >= 0 ? Quests[idx] : (QuestProgress?)null;
        }

        public bool HasQuest(string questId)        => FindIndexById(questId) >= 0;

        public bool IsActive(string questId)
        {
            var p = FindByIdNullable(questId);
            return p.HasValue && p.Value.State == QuestState.Active;
        }

        public bool IsReadyToTurnIn(string questId)
        {
            var p = FindByIdNullable(questId);
            return p.HasValue && p.Value.State == QuestState.ReadyToTurnIn;
        }

        public bool IsCompleted(string questId)
        {
            var p = FindByIdNullable(questId);
            return p.HasValue && p.Value.State == QuestState.Completed;
        }

        public int CountActive()
        {
            int n = 0;
            foreach (var q in Quests)
                if (q.State == QuestState.Active || q.State == QuestState.ReadyToTurnIn) n++;
            return n;
        }

        // ══════════════════════════════════════════════════════════════════
        // SERVIDOR — Offer / Accept / Complete
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Determina o que um NPC deve oferecer ao jogador para uma lista
        /// de QuestIds. Resultado pode ser:
        ///   - Offer: quest disponível para aceitar
        ///   - InProgress: jogador já tem ativa
        ///   - TurnIn: jogador completou objetivos e pode entregar
        ///   - Locked: pré-requisitos não atendidos
        ///   - AlreadyDone: já completada (não repetível)
        /// </summary>
        [Server]
        public NpcQuestOption EvaluateQuestForOffer(string questId)
        {
            var def = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null) return NpcQuestOption.Invalid(questId);

            var existing = FindByIdNullable(questId);

            if (existing.HasValue)
            {
                switch (existing.Value.State)
                {
                    case QuestState.Active:        return NpcQuestOption.InProgress(questId);
                    case QuestState.ReadyToTurnIn: return NpcQuestOption.TurnIn(questId);
                    case QuestState.Completed:
                        if (def.Repeatable)
                            return PrerequisitesMet(def)
                                ? NpcQuestOption.Offer(questId)
                                : NpcQuestOption.Locked(questId);
                        return NpcQuestOption.AlreadyDone(questId);
                    case QuestState.Failed:
                        return NpcQuestOption.Locked(questId);
                }
            }

            return PrerequisitesMet(def)
                ? NpcQuestOption.Offer(questId)
                : NpcQuestOption.Locked(questId);
        }

        [Server]
        private bool PrerequisitesMet(QuestDefinition def)
        {
            if (_netPlayer == null) return false;
            if (_netPlayer.Level < def.RequiredLevel) return false;

            if (def.RequiredCompletedQuests != null)
            {
                foreach (var reqId in def.RequiredCompletedQuests)
                {
                    if (string.IsNullOrEmpty(reqId)) continue;
                    if (!IsCompleted(reqId)) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Tenta aceitar uma quest. Valida tudo server-side.
        /// Retorna true em sucesso; em falha, envia mensagem ao owner via Rpc.
        /// </summary>
        [Server]
        public bool ServerAcceptQuest(string questId, out string reason)
        {
            reason = null;

            var def = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null)
            {
                reason = "Quest desconhecida.";
                return false;
            }

            if (CountActive() >= MAX_ACTIVE_QUESTS)
            {
                reason = $"Limite de {MAX_ACTIVE_QUESTS} quests ativas atingido.";
                return false;
            }

            var existing = FindByIdNullable(questId);
            if (existing.HasValue)
            {
                if (existing.Value.State == QuestState.Active
                    || existing.Value.State == QuestState.ReadyToTurnIn)
                {
                    reason = "Você já tem esta quest.";
                    return false;
                }
                if (existing.Value.State == QuestState.Completed && !def.Repeatable)
                {
                    reason = "Você já completou esta quest.";
                    return false;
                }
            }

            if (!PrerequisitesMet(def))
            {
                reason = $"Requer nível {def.RequiredLevel}";
                if (def.RequiredCompletedQuests != null && def.RequiredCompletedQuests.Length > 0)
                    reason += " ou quests anteriores.";
                else
                    reason += ".";
                return false;
            }

            var newEntry = QuestProgress.NewActive(questId, def.ObjectiveCount);

            int existingIdx = FindIndexById(questId);
            if (existingIdx >= 0)
                Quests[existingIdx] = newEntry;
            else
                Quests.Add(newEntry);

            // Para quests de "collect": já pode estar parcialmente completa via inventário
            RecheckCollectObjectives(questId);
            // Para "reach level": idem
            RecheckLevelObjectives(questId);

            _netPlayer?.RpcShowMessageToOwner($"Quest aceita: {def.DisplayName}");
            return true;
        }

        /// <summary>
        /// Tenta completar e entregar a quest (jogador retornou ao NPC).
        /// </summary>
        [Server]
        public bool ServerCompleteQuest(string questId, out string reason)
        {
            reason = null;

            int idx = FindIndexById(questId);
            if (idx < 0)
            {
                reason = "Você não aceitou esta quest.";
                return false;
            }

            var progress = Quests[idx];
            var def      = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null) { reason = "Quest desconhecida."; return false; }

            if (progress.State != QuestState.ReadyToTurnIn)
            {
                if (progress.State == QuestState.Completed)
                    reason = "Você já completou esta quest.";
                else
                    reason = "Objetivos ainda não foram completados.";
                return false;
            }

            // Distribui recompensa
            ApplyReward(def);

            // Para quests não repetíveis, marca como Completed.
            // Para repetíveis, REMOVE do tracker (ou marca como Completed e
            // permite re-aceitar). Escolha de design: REMOVER para não
            // poluir a SyncList com infinitos completes de quest diária.
            if (def.Repeatable)
                Quests.RemoveAt(idx);
            else
                Quests[idx] = progress.WithState(QuestState.Completed);

            // Cap defensivo no crescimento da lista (improvável mas defesa)
            EnforceCompletedQuestsCap();

            _netPlayer?.RpcShowMessageToOwner($"Quest completa: {def.DisplayName}!");
            ServerSaveAll();
            return true;
        }

        [Server]
        private void ApplyReward(QuestDefinition def)
        {
            if (def.Reward == null) return;

            if (def.Reward.Experience > 0)
                _netPlayer?.ServerGrantExp(def.Reward.Experience);

            if (def.Reward.Items != null && _inventory != null)
            {
                foreach (var item in def.Reward.Items)
                {
                    if (item == null || string.IsNullOrEmpty(item.ItemId)) continue;
                    if (item.Quantity <= 0) continue;
                    _inventory.ServerAddItem(item.ItemId, item.Quantity);
                }
            }
            // UnlocksQuestId é apenas informativo — checagem ocorre em
            // PrerequisitesMet quando o jogador tenta aceitar a próxima.
        }

        [Server]
        private void EnforceCompletedQuestsCap()
        {
            if (Quests.Count <= MAX_COMPLETED_QUESTS_TRACKED) return;

            // Remove as Completed mais antigas até voltar ao cap. Mantém
            // todas as ativas e prontas, intocadas.
            int over = Quests.Count - MAX_COMPLETED_QUESTS_TRACKED;
            int removed = 0;

            // Procura a Completed com menor StateTimestamp e remove
            while (over > 0 && removed < 100) // cap interno de iterações
            {
                int oldestIdx = -1;
                long oldestTs = long.MaxValue;
                for (int i = 0; i < Quests.Count; i++)
                {
                    if (Quests[i].State != QuestState.Completed) continue;
                    if (Quests[i].StateTimestamp < oldestTs)
                    {
                        oldestTs = Quests[i].StateTimestamp;
                        oldestIdx = i;
                    }
                }
                if (oldestIdx < 0) break;
                Quests.RemoveAt(oldestIdx);
                removed++;
                over--;
            }

            if (removed > 0)
                Debug.LogWarning($"[QuestManager] Cap de quests completadas atingido — removidas {removed} antigas.");
        }

        // ══════════════════════════════════════════════════════════════════
        // SERVIDOR — Notificação de eventos do mundo
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Avança progresso de TODAS as quests ativas que tenham objetivo
        /// matching com este evento. Chamado por:
        ///   - NetworkMonsterEntity após morte (KillMonster)
        ///   - NetworkInventory após pickup (CollectItem)
        ///   - NetworkPlayer após level up (ReachLevel)
        ///   - NetworkNPC após diálogo concluído (TalkToNPC)
        ///   - QuestZone trigger (ReachLocation)
        /// </summary>
        [Server]
        public void NotifyEvent(QuestObjectiveType type, string targetId, int delta = 1)
        {
            if (delta <= 0) return;

            for (int i = 0; i < Quests.Count; i++)
            {
                var entry = Quests[i];
                if (entry.State != QuestState.Active) continue;

                var def = QuestDatabase.Instance?.GetQuest(entry.QuestId);
                if (def == null) continue;
                if (def.ObjectiveCount == 0) continue;

                var progress = entry.GetProgressArray(def.ObjectiveCount);
                bool changed = false;

                for (int o = 0; o < def.ObjectiveCount; o++)
                {
                    var obj = def.GetObjective(o);
                    if (obj == null) continue;
                    if (obj.Type != type) continue;
                    if (!def.CanObjectiveBeAdvanced(o, progress)) continue;

                    bool matches = obj.Type == QuestObjectiveType.ReachLevel
                        ? IsLevelObjectiveMet(obj.TargetCount)
                        : string.Equals(obj.TargetId, targetId, StringComparison.Ordinal);

                    if (!matches) continue;
                    if (progress[o] >= obj.TargetCount) continue;

                    int newValue = obj.Type == QuestObjectiveType.ReachLevel
                        ? obj.TargetCount                 // ReachLevel: marca como completo direto
                        : Math.Min(obj.TargetCount, progress[o] + delta);

                    progress[o] = newValue;
                    changed = true;
                }

                if (!changed) continue;

                var newState = AllObjectivesComplete(def, progress)
                    ? QuestState.ReadyToTurnIn
                    : QuestState.Active;

                var newEntry = entry.WithProgress(progress, newState);
                Quests[i] = newEntry;

                if (newState == QuestState.ReadyToTurnIn && def.AutoComplete)
                {
                    // Entrega automática (tutoriais, quests sem NPC)
                    if (ServerCompleteQuest(entry.QuestId, out _))
                    {
                        // Quest pode ter saído do índice atual (RemoveAt em repeatable),
                        // mas como não estamos dentro de loop dependente da remoção,
                        // basta voltar um índice por segurança.
                        i = Math.Max(-1, i - 1);
                    }
                }
            }
        }

        [Server]
        private bool IsLevelObjectiveMet(int targetLevel)
            => _netPlayer != null && _netPlayer.Level >= targetLevel;

        [Server]
        private static bool AllObjectivesComplete(QuestDefinition def, int[] progress)
        {
            if (def.Objectives == null) return false;
            for (int i = 0; i < def.Objectives.Length; i++)
            {
                if (progress[i] < def.Objectives[i].TargetCount) return false;
            }
            return true;
        }

        /// <summary>
        /// Re-conta itens no inventário do jogador para todos os objetivos
        /// CollectItem da quest informada. Chamado:
        ///   - Ao aceitar (caso já tenha itens)
        ///   - Após pickup (NetworkInventory)
        /// </summary>
        [Server]
        public void RecheckCollectObjectives(string questId)
        {
            if (_inventory == null) return;

            int idx = FindIndexById(questId);
            if (idx < 0) return;

            var entry = Quests[idx];
            if (entry.State != QuestState.Active) return;

            var def = QuestDatabase.Instance?.GetQuest(questId);
            if (def == null) return;

            var progress = entry.GetProgressArray(def.ObjectiveCount);
            bool changed = false;

            for (int o = 0; o < def.ObjectiveCount; o++)
            {
                var obj = def.GetObjective(o);
                if (obj == null || obj.Type != QuestObjectiveType.CollectItem) continue;
                if (!def.CanObjectiveBeAdvanced(o, progress)) continue;

                int count = _inventory.GetTotalQuantity(obj.TargetId);
                int capped = Math.Min(count, obj.TargetCount);
                if (progress[o] != capped)
                {
                    progress[o] = capped;
                    changed = true;
                }
            }

            if (changed)
            {
                var newState = AllObjectivesComplete(def, progress)
                    ? QuestState.ReadyToTurnIn
                    : QuestState.Active;
                Quests[idx] = entry.WithProgress(progress, newState);

                if (newState == QuestState.ReadyToTurnIn && def.AutoComplete)
                    ServerCompleteQuest(questId, out _);
            }
        }

        /// <summary>Chamado quando o jogador sobe de nível.</summary>
        [Server]
        public void NotifyLevelUp(int newLevel)
        {
            NotifyEvent(QuestObjectiveType.ReachLevel, "", delta: 1);
        }

        [Server]
        private void RecheckLevelObjectives(string questId)
        {
            NotifyLevelUp(_netPlayer?.Level ?? 1);
        }

        // ══════════════════════════════════════════════════════════════════
        // SERVIDOR — Persistência
        // ══════════════════════════════════════════════════════════════════

        [Server]
        public void ServerLoadFromDatabase(string characterId, string accountUsername)
        {
            _serverCharacterId     = characterId;
            _serverAccountUsername = accountUsername;

            var db = DatabaseManager.Instance;
            if (db == null) return;

            Quests.Clear();

            var rows = db.LoadQuestProgress(characterId);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.QuestId)) continue;

                // Filtra IDs órfãos (quest removida em patch)
                if (QuestDatabase.Instance != null && !QuestDatabase.Instance.Contains(row.QuestId))
                {
                    Debug.LogWarning($"[QuestManager] Quest '{row.QuestId}' do banco não está no QuestDatabase — ignorada.");
                    continue;
                }

                Quests.Add(new QuestProgress
                {
                    QuestId        = row.QuestId,
                    State          = (QuestState)Math.Max(0, Math.Min(row.State, (int)QuestState.Failed)),
                    ProgressCsv    = row.ProgressCsv ?? "",
                    StateTimestamp = row.StateTimestamp
                });
            }
        }

        [Server]
        public void ServerSaveAll()
        {
            if (string.IsNullOrEmpty(_serverCharacterId)) return;
            var db = DatabaseManager.Instance;
            if (db == null) return;

            var snapshot = new List<QuestProgress>(Quests);
            db.SaveQuestProgress(_serverCharacterId, snapshot);
        }

        // ══════════════════════════════════════════════════════════════════
        // CLIENTE → SERVIDOR (Commands)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cliente solicita aceitar uma quest oferecida por um NPC.
        /// O servidor revalida que o jogador está realmente perto do NPC
        /// que oferece esta quest. (Defesa contra cliente malformado.)
        /// </summary>
        [Command]
        public void CmdAcceptQuest(uint npcNetId, string questId)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            // Resolve NPC e revalida proximidade
            if (!NetworkServer.spawned.TryGetValue(npcNetId, out var identity)
                || identity == null)
            {
                _netPlayer.RpcShowMessageToOwner("NPC não encontrado.");
                return;
            }

            var npc = identity.GetComponent<RPG.NPC.NetworkNPC>();
            if (npc == null)
            {
                _netPlayer.RpcShowMessageToOwner("NPC inválido.");
                return;
            }

            if (!npc.IsPlayerInInteractionRange(transform.position))
            {
                _netPlayer.RpcShowMessageToOwner("Você está longe demais.");
                return;
            }

            if (!npc.OffersQuest(questId))
            {
                _netPlayer.RpcShowMessageToOwner("Este NPC não oferece esta quest.");
                return;
            }

            if (!ServerAcceptQuest(questId, out string reason))
                _netPlayer.RpcShowMessageToOwner(reason);
        }

        [Command]
        public void CmdCompleteQuest(uint npcNetId, string questId)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            if (!NetworkServer.spawned.TryGetValue(npcNetId, out var identity)
                || identity == null) return;

            var npc = identity.GetComponent<RPG.NPC.NetworkNPC>();
            if (npc == null) return;

            if (!npc.IsPlayerInInteractionRange(transform.position))
            {
                _netPlayer.RpcShowMessageToOwner("Você está longe demais.");
                return;
            }

            if (!npc.OffersQuest(questId))
            {
                _netPlayer.RpcShowMessageToOwner("Este NPC não pode receber esta quest.");
                return;
            }

            if (!ServerCompleteQuest(questId, out string reason))
                _netPlayer.RpcShowMessageToOwner(reason);
        }

        /// <summary>Player abandona uma quest ativa via UI do log.</summary>
        [Command]
        public void CmdAbandonQuest(string questId)
        {
            if (connectionToClient == null) return;
            if (_netPlayer == null || _netPlayer.Dead) return;

            int idx = FindIndexById(questId);
            if (idx < 0) return;

            var entry = Quests[idx];
            // Só Active/ReadyToTurnIn podem ser abandonadas.
            if (entry.State != QuestState.Active && entry.State != QuestState.ReadyToTurnIn) return;

            Quests.RemoveAt(idx);
            _netPlayer.RpcShowMessageToOwner("Quest abandonada.");
            ServerSaveAll();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Tipo auxiliar — usado por NetworkNPC para enviar opções para o cliente
    // ══════════════════════════════════════════════════════════════════════

    public enum NpcQuestOptionState : byte
    {
        Invalid     = 0,
        Offer       = 1,
        InProgress  = 2,
        TurnIn      = 3,
        Locked      = 4,
        AlreadyDone = 5,
    }

    [Serializable]
    public struct NpcQuestOption
    {
        public string              QuestId;
        public NpcQuestOptionState State;

        public static NpcQuestOption Invalid(string id)     => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.Invalid };
        public static NpcQuestOption Offer(string id)       => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.Offer };
        public static NpcQuestOption InProgress(string id)  => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.InProgress };
        public static NpcQuestOption TurnIn(string id)      => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.TurnIn };
        public static NpcQuestOption Locked(string id)      => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.Locked };
        public static NpcQuestOption AlreadyDone(string id) => new NpcQuestOption { QuestId = id, State = NpcQuestOptionState.AlreadyDone };
    }
}
