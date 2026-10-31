using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using System.Collections;

namespace SpookyBavaria
{
    public class CreateAudioOnDemand : MonoBehaviour
    {
        public Dictionary<string, AudioClip> audioClips = new Dictionary<string,AudioClip>();

        public void CreateAudioClips<T>(T instance, Func<string[]> action, Action whenDone) where T : MonoBehaviour
        {
            T[] instances = FindObjectsByType<T>(FindObjectsSortMode.None);
            if (instances.Length > 1 && audioClips.Count != 0)
            {
                return;
            }
            var audioclipNames = action();
            for (int i = 0; i < audioclipNames.Length; i++)
            {
                StartCoroutine(ModExternalAssets.LoadAudioStream(this, audioclipNames[i], whenDone));
            }
        }

        void OnDestroy()
        {
            var keys = audioClips.Keys.ToArray();
            for (int i = 0; i < keys.Length; i++)
            {
                DestroyImmediate(audioClips[keys[i]]);
                Debug.Log(audioClips[keys[i]]);
            }
            audioClips.Clear();
            Resources.UnloadUnusedAssets();
            Debug.Log(gameObject.name + " is being destroyed");
        }
    }
}
