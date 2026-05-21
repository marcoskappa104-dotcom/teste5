using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Character;
using RPG.Quest;

namespace RPG.NPC
{
    /// <summary>
    /// NPC interagível: mercador, questgiver, treinador, etc.
    /// É também ITargetable (jogador pode selecionar com clique).
    ///
    /// === SETUP NO EDITOR ===
    ///   1. Prefab precisa ter: NetworkIdentity (Server Only Auth), Collider
    ///      em layer "Targetable", e este componente.
    ///   2. Defina NpcId (kebab-case, único e estável).
    ///   3. Configure DefaultGreeting e arraste QuestDefinitions para
    ///      OfferedQuests se for um questgiver.
    ///   4. Adicione o prefab em RPGNetworkManager.spawnablePrefabs.
    ///
    /// === COMPORTAMENTO ===
    ///   - NPCs são spawnados via NPCSpawner (similar a MonsterSpawner).
    ///   - Server-authoritative: NPCs não se movem por enquanto (fixos).
    ///     Movimento futuro pode ser adicionado sem quebrar a API.
    ///   - Interação: jogador clica → NPCInteractor envia CmdInteract →
    ///     servidor valida range/cooldown → envia TargetRpc com opções →
    ///     DialogUI abre.
    ///
    /// === SEGURANÇA ===
    ///   - Range validado server-side em CmdInteract.
    ///   - Cooldown de interação para evitar spam.
    ///   - QuestManager revalida tudo de novo antes de aceitar/completar.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkNPC : NetworkBehaviour, ITargetable
    {
        // ── Constantes ─────────────────────────────────────────────────────
        private const float MIN_INTERACT_COOLDOWN  = 0.3f;

        // ── Identidade ─────────────────────────────────────────────────────
        [Header("Identidade")]
        [Tooltip("ID único e estável. Use em quests (TalkToNPC TargetId).")]
        [SerializeField] private string npcId = "npc_unknown";

        [Tooltip("Nome exibido em UI/tooltip/dialog header.")]
        [SerializeField] private string displayName = "NPC";

        [Tooltip("Função (mercador, ferreiro, etc.) — apenas cosmético.")]
        [SerializeField] private string role = "Aldeão";

        // ── Diálogo ────────────────────────────────────────────────────────
        [Header("Diálogo")]
        [Tooltip("Frase exibida quando o jogador interage e o NPC não tem nada específico.")]
        [TextArea(2, 4)]
        [SerializeField] private string defaultGreeting = "Olá, aventureiro!";

        [Tooltip("Frase exibida quando o jogador já completou tudo que este NPC oferece.")]
        [TextArea(2, 4)]
        [SerializeField] private string noQuestsGreeting = "Volte mais tarde, talvez eu tenha algo para você.";

        // ── Quests ─────────────────────────────────────────────────────────
        [Header("Quests")]
        [Tooltip("Quests que este NPC oferece. Suporta múltiplas — o cliente vê todas as disponíveis.")]
        [SerializeField] private QuestDefinition[] offeredQuests = new QuestDefinition[0];

        // ── Interação ──────────────────────────────────────────────────────
        [Header("Interação")]
        [Tooltip("Distância máxima do jogador para interagir (server-side).")]
        [SerializeField] private float interactionRange = 4f;

        [Tooltip("Tolerância anti-cheat: range * este multiplicador é o limite real.")]
        [SerializeField] private float interactionRangeTolerance = 1.5f;

        [Tooltip("Cooldown entre interações pelo MESMO jogador (anti-spam).")]
        [SerializeField] private float interactCooldown = 0.5f;

        // ── Visual ─────────────────────────────────────────────────────────
        [Header("Visual")]
        [SerializeField] private GameObject selectionIndicator;
        [SerializeField] private TMPro.TMP_Text nameTagText;
        [Tooltip("Ícone flutuante quando NPC tem quest disponível (!) ou turn-in (?).")]
        [SerializeField] private GameObject questAvailableMarker;
        [SerializeField] private GameObject questTurnInMarker;
        [Tooltip("Visual root usado para encarar a câmera (opcional).")]
        [SerializeField] private Transform billboardTransform;

        // ── ITargetable ────────────────────────────────────────────────────
        public string  NpcId       => npcId;
        public string  DisplayName => displayName;
        public string  Role        => role;
        public float   CurrentHP   => 1f;            // NPCs imortais
        public float   MaxHP       => 1f;
        public bool    IsDead      => false;
        public Vector3 Position    => transform.position;

        public float InteractionRangeReal => interactionRange * Mathf.Max(1f, interactionRangeTolerance);

        public void OnSelected()   { if (selectionIndicator != null) selectionIndicator.SetActive(true);  }
        public void OnDeselected() { if (selectionIndicator != null) selectionIndicator.SetActive(false); }

        // ── Estado server-side ─────────────────────────────────────────────
        // Cooldown por jogador: chave = connectionId, valor = Time.time
        private readonly Dictionary<int, float> _interactCooldowns = new Dictionary<int, float>();

        // Cache leve do marcador de quest (atualizado em hover/interact)
        private bool _hasQuestMarker;
        private bool _hasTurnInMarker;

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (selectionIndicator != null) selectionIndicator.SetActive(false);
            if (questAvailableMarker != null) questAvailableMarker.SetActive(false);
            if (questTurnInMarker != null) questTurnInMarker.SetActive(false);

            if (nameTagText != null)
                nameTagText.text = !string.IsNullOrEmpty(role)
                    ? $"{displayName}\n<size=70%><color=#AAAAAA>{role}</color></size>"
                    : displayName;
        }

        private void LateUpdate()
        {
            // Billboard simples para o nameTag e marcadores
            if (Application.isBatchMode) return;
            if (billboardTransform == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            billboardTransform.forward = (billboardTransform.position - cam.transform.position).normalized;
        }

        // ══════════════════════════════════════════════════════════════════
        // API server-side
        // ══════════════════════════════════════════════════════════════════

        /// <summary>True se este NPC oferece a quest informada.</summary>
        public bool OffersQuest(string questId)
        {
            if (string.IsNullOrEmpty(questId)) return false;
            if (offeredQuests == null) return false;
            for (int i = 0; i < offeredQuests.Length; i++)
            {
                if (offeredQuests[i] == null) continue;
                if (offeredQuests[i].QuestId == questId) return true;
            }
            return false;
        }

        public bool IsPlayerInInteractionRange(Vector3 playerPosition)
        {
            float dist = Vector3.Distance(transform.position, playerPosition);
            return dist <= InteractionRangeReal;
        }

        /// <summary>
        /// Avalia o estado das quests deste NPC para um jogador específico.
        /// Útil para decidir qual marcador mostrar (!/?/nada).
        /// </summary>
        [Server]
        public NpcInteractionSnapshot ServerBuildSnapshotFor(RPG.Network.NetworkPlayer player)
        {
            var snap = new NpcInteractionSnapshot
            {
                NpcId       = npcId,
                DisplayName = displayName,
                Role        = role,
                Greeting    = defaultGreeting
            };

            if (player == null || offeredQuests == null || offeredQuests.Length == 0)
            {
                snap.Greeting = !string.IsNullOrEmpty(defaultGreeting) ? defaultGreeting : noQuestsGreeting;
                snap.Options  = new List<NpcQuestOption>();
                return snap;
            }

            var questManager = player.GetComponent<QuestManager>();
            var options      = new List<NpcQuestOption>(offeredQuests.Length);

            foreach (var def in offeredQuests)
            {
                if (def == null) continue;
                if (questManager == null)
                {
                    options.Add(NpcQuestOption.Locked(def.QuestId));
                    continue;
                }
                options.Add(questManager.EvaluateQuestForOffer(def.QuestId));
            }

            snap.Options = options;

            // Greeting muda se NPC só tem coisas "Locked" ou "AlreadyDone"
            bool hasOffer  = false;
            bool hasTurnIn = false;
            foreach (var op in options)
            {
                if (op.State == NpcQuestOptionState.Offer) hasOffer = true;
                if (op.State == NpcQuestOptionState.TurnIn) hasTurnIn = true;
            }
            if (!hasOffer && !hasTurnIn && options.Count > 0)
                snap.Greeting = noQuestsGreeting;

            return snap;
        }

        // ══════════════════════════════════════════════════════════════════
        // Command de interação
        // ══════════════════════════════════════════════════════════════════

        [Command(requiresAuthority = false)]
        public void CmdInteract(NetworkConnectionToClient sender = null)
        {
            if (sender == null) return;
            var ownerIdentity = sender.identity;
            if (ownerIdentity == null) return;

            var player = ownerIdentity.GetComponent<RPG.Network.NetworkPlayer>();
            if (player == null || player.Dead) return;

            // Cooldown por jogador
            float cooldown = Mathf.Max(MIN_INTERACT_COOLDOWN, interactCooldown);
            if (_interactCooldowns.TryGetValue(sender.connectionId, out float nextAllowed)
                && Time.time < nextAllowed)
            {
                return;
            }
            _interactCooldowns[sender.connectionId] = Time.time + cooldown;

            // Range check
            if (!IsPlayerInInteractionRange(player.transform.position))
            {
                player.RpcShowMessageToOwner("Você está longe demais para falar.");
                return;
            }

            // Constrói snapshot e envia ao jogador
            var snapshot = ServerBuildSnapshotFor(player);
            TargetOpenDialog(sender, this.netId, snapshot);
        }

        // ══════════════════════════════════════════════════════════════════
        // Server → Cliente owner
        // ══════════════════════════════════════════════════════════════════

        [TargetRpc]
        private void TargetOpenDialog(NetworkConnectionToClient target,
                                      uint npcNetId,
                                      NpcInteractionSnapshot snapshot)
        {
            if (Application.isBatchMode) return;
            RPG.UI.DialogUI.Instance?.OpenForNpc(npcNetId, snapshot);
        }

        // ══════════════════════════════════════════════════════════════════
        // Quest marker visual (atualizado pelo cliente em resposta a sync)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Chamado pelo NPCQuestMarkerUpdater (cliente) quando o jogador local
        /// muda. Garante que o marcador acima do NPC reflita o estado das quests
        /// deste jogador específico.
        /// </summary>
        public void ClientUpdateQuestMarker(RPG.Network.NetworkPlayer localPlayer)
        {
            if (Application.isBatchMode) return;
            if (offeredQuests == null || offeredQuests.Length == 0)
            {
                SetMarkerStates(false, false);
                return;
            }

            if (localPlayer == null)
            {
                SetMarkerStates(false, false);
                return;
            }

            var qm = localPlayer.GetComponent<QuestManager>();
            if (qm == null)
            {
                SetMarkerStates(false, false);
                return;
            }

            bool hasOffer  = false;
            bool hasTurnIn = false;

            // Recria EvaluateQuestForOffer no cliente (idempotente e barato).
            // Não tem informação privilegiada — apenas leitura.
            foreach (var def in offeredQuests)
            {
                if (def == null) continue;

                var existing = qm.FindByIdNullable(def.QuestId);
                if (existing.HasValue)
                {
                    if (existing.Value.State == QuestState.ReadyToTurnIn) { hasTurnIn = true; }
                    else if (existing.Value.State == QuestState.Completed && !def.Repeatable) { /* nada */ }
                    else if (existing.Value.State == QuestState.Completed && def.Repeatable
                             && ClientPrerequisitesMet(localPlayer, qm, def)) { hasOffer = true; }
                }
                else if (ClientPrerequisitesMet(localPlayer, qm, def))
                {
                    hasOffer = true;
                }
            }

            SetMarkerStates(hasOffer, hasTurnIn);
        }

        private static bool ClientPrerequisitesMet(RPG.Network.NetworkPlayer player,
                                                   QuestManager qm,
                                                   QuestDefinition def)
        {
            if (player.Level < def.RequiredLevel) return false;
            if (def.RequiredCompletedQuests != null)
            {
                foreach (var reqId in def.RequiredCompletedQuests)
                {
                    if (string.IsNullOrEmpty(reqId)) continue;
                    if (!qm.IsCompleted(reqId)) return false;
                }
            }
            return true;
        }

        private void SetMarkerStates(bool offer, bool turnIn)
        {
            // Turn-in tem prioridade visual sobre offer
            if (turnIn)
            {
                if (questTurnInMarker   != null) questTurnInMarker.SetActive(true);
                if (questAvailableMarker != null) questAvailableMarker.SetActive(false);
            }
            else if (offer)
            {
                if (questAvailableMarker != null) questAvailableMarker.SetActive(true);
                if (questTurnInMarker    != null) questTurnInMarker.SetActive(false);
            }
            else
            {
                if (questAvailableMarker != null) questAvailableMarker.SetActive(false);
                if (questTurnInMarker    != null) questTurnInMarker.SetActive(false);
            }

            _hasQuestMarker  = offer;
            _hasTurnInMarker = turnIn;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, interactionRange);

            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, InteractionRangeReal);
        }

        protected override void OnValidate()
        {
            base.OnValidate();
            if (interactionRange < 0.5f) interactionRange = 0.5f;
            if (interactionRangeTolerance < 1f) interactionRangeTolerance = 1f;
            if (interactCooldown < MIN_INTERACT_COOLDOWN) interactCooldown = MIN_INTERACT_COOLDOWN;
        }
#endif
    }

    // ══════════════════════════════════════════════════════════════════════
    // Snapshot trafegado pela rede (NPC → cliente owner via TargetRpc)
    // ══════════════════════════════════════════════════════════════════════

    [System.Serializable]
    public struct NpcInteractionSnapshot
    {
        public string                NpcId;
        public string                DisplayName;
        public string                Role;
        public string                Greeting;
        public List<NpcQuestOption>  Options;
    }
}