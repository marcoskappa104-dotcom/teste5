using UnityEngine;
using Mirror;
using RPG.Character;
using RPG.UI;

namespace RPG.NPC
{
    /// <summary>
    /// Lado cliente da interação com NPCs.
    ///
    /// Anexar ao playerPrefab. Quando o jogador clica num NPC, este componente:
    ///   1. Seleciona o NPC como target (mesma lógica de monstros).
    ///   2. Verifica range — se fora, NÃO move o player automaticamente
    ///      (decisão de design: jogadores devem se aproximar manualmente,
    ///      como Ragnarok / clássicos). Mostra mensagem.
    ///   3. Se em range, envia CmdInteract.
    ///
    /// NÃO faz raycast por conta própria — é invocado por NetworkPlayerController
    /// quando o raycast em targetableLayer atinge um NetworkNPC.
    /// </summary>
    public class NPCInteractor : MonoBehaviour
    {
        [Tooltip("Margem extra além do range do NPC para chamar 'em alcance'. " +
                 "Cliente é otimista; servidor é o juiz final.")]
        [SerializeField] private float clientRangeBonus = 0.5f;

        private NetworkIdentity _identity;
        private PlayerEntity    _playerEntity;

        private void Awake()
        {
            _identity     = GetComponent<NetworkIdentity>();
            _playerEntity = GetComponent<PlayerEntity>();
        }

        /// <summary>
        /// Tenta interagir com um NPC. Retorna true se a interação foi
        /// disparada (range OK); false se o jogador precisa se aproximar.
        /// </summary>
        public bool TryInteract(NetworkNPC npc)
        {
            if (npc == null) return false;
            if (_identity == null) return false;
            if (_playerEntity != null && _playerEntity.IsDead) return false;

            // Seleciona como target
            _playerEntity?.SetTarget(npc);
            UIManager.Instance?.UpdateTargetPanel(npc);

            float dist     = Vector3.Distance(transform.position, npc.Position);
            float maxRange = npc.InteractionRangeReal + clientRangeBonus;
            if (dist > maxRange)
            {
                UIManager.Instance?.ShowMessage("Aproxime-se para conversar.");
                return false;
            }

            // Envia comando. Servidor revalida e responde com TargetRpc.
            npc.CmdInteract();
            return true;
        }
    }
}
