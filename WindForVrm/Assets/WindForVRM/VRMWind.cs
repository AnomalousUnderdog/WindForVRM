using System.Collections.Generic;
using UniGLTF.SpringBoneJobs.Blittables;
using UnityEngine;
using UniVRM10;

namespace WindForVRM
{
    /// <summary>
    /// VRMの揺れものに対して風を発生させる発生源の処理
    /// </summary>
    public class VRMWind : MonoBehaviour
    {
        //ふわっと強くなってから弱まる一連の動きを表現する、個別の風要素
        struct WindItem
        {
            public WindItem(Vector3 orientation, float riseCount, float sitCount, float maxFactor)
            {
                Orientation = orientation;
                RiseCount = riseCount;
                SitCount = sitCount;
                MaxFactor = maxFactor;

                TimeCount = 0;
                TotalTime = RiseCount + SitCount;
            }

            public Vector3 Orientation { get; }
            public float RiseCount { get; }
            public float SitCount { get; }
            public float MaxFactor { get; }
            public float TotalTime { get; }
            public float TimeCount { get; set; }

            public float CurrentFactor =>
                TimeCount < RiseCount
                    ? MaxFactor * TimeCount / RiseCount
                    : MaxFactor * (1 - (TimeCount - RiseCount) / SitCount);
        }

        [Tooltip("If assigned, only Spring Bone Joints found in these transforms will be affected by the wind.\nIf empty, all Spring Bone Joints from the root of the VRM will be affected.\n\nUse this to exclude certain Spring Bone Joints from being affected by the wind (such as the bust, for example).")]
        [SerializeField] private List<Transform> affectedByWind;

        [Tooltip("このコンポーネントがVRMアバターにアタッチされており、自動で初期化したい場合はチェックをオンにします。")]
        [SerializeField] private bool loadAutomatic = false;

        [Tooltip("風の計算を有効化するかどうか")]
        [SerializeField] private bool enableWind = true;

        [Tooltip("基本になる風向き。ワールド座標で指定します。")]
        [SerializeField] private Vector3 windBaseOrientation = Vector3.right;

        [Tooltip("風向きをちょっとランダムにするためのファクタ")]
        [SerializeField] private float windOrientationRandomPower = 0.2f;

        //風の強さ、発生頻度、立ち上がりと立ち下がりの時間を、それぞれ全てRandom.Rangeに通すために幅付きの値にする
        [SerializeField] private Vector2 windStrengthRange = new Vector2(0.03f, 0.06f);
        [SerializeField] private Vector2 windIntervalRange = new Vector2(0.7f, 1.9f);
        [SerializeField] private Vector2 windRiseCountRange = new Vector2(0.4f, 0.6f);
        [SerializeField] private Vector2 windSitCountRange = new Vector2(1.3f, 1.8f);

        //上記の強さと時間を定数倍するファクタ
        [SerializeField] private float strengthFactor = 1.0f;
        [SerializeField] private float timeFactor = 1.0f;

        private IVrm10SpringBoneRuntime _springBoneController;
        private float _windGenerateCount = 0;
        private readonly List<VRM10SpringBoneJoint> _springBones = new List<VRM10SpringBoneJoint>();
        private Vector3[] _originalGravityDirections = new Vector3[] { };
        private float[] _originalGravityFactors = new float[] { };
        private readonly List<WindItem> _windItems = new List<WindItem>();

        /// <summary> 風の計算を有効にするかどうかを取得、設定します。 </summary>
        public bool EnableWind
        {
            get => enableWind;
            set
            {
                if (enableWind == value)
                {
                    return;
                }
                enableWind = value;
                if (!value)
                {
                    DisableWind();
                }
            }
        }

        /// <summary> 風の方向をワールド座標で取得、設定します。 </summary>
        public Vector3 WindBaseOrientation
        {
            get => windBaseOrientation;
            set => windBaseOrientation = value;
        }

        /// <summary> 風の方向をランダム化する強さを0から1程度の範囲で取得、設定します。 </summary>
        public float WindOrientationRandomPower
        {
            get => windOrientationRandomPower;
            set => windOrientationRandomPower = value;
        }

        /// <summary> 風の強さを倍率として取得、設定します。大きな値にするほど強い風が吹いている扱いになります。 </summary>
        public float StrengthFactor
        {
            get => strengthFactor;
            set => strengthFactor = value;
        }

        /// <summary> 個別の風要素を生成する間隔を倍率で取得、設定します。小さい値にするほど、風が細かく生成されます。 </summary>
        public float TimeFactor
        {
            get => timeFactor;
            set => timeFactor = value;
        }


        /// <summary>
        /// 対象となるVRMのルート要素を指定してVRMを読み込みます。
        /// loadAutomaticがオンで、あらかじめこのコンポーネントがVRMにアタッチされている場合、呼び出しは不要です。
        /// </summary>
        /// <param name="vrmRoot"></param>
        public void LoadVrm(Transform vrmRoot)
        {
            var vrmInstance = vrmRoot.GetComponent<Vrm10Instance>();
            if (vrmInstance == null)
            {
                Debug.LogError($"For VRMWind, {vrmRoot.name} needs to have a Vrm10Instance component. Aborting.", vrmRoot);
                enabled = false;
                return;
            }

            _springBoneController = vrmInstance.Runtime.SpringBone;

            _springBones.Clear();
            if (affectedByWind.Count > 0)
            {
                var gotJointsBuffer = new List<VRM10SpringBoneJoint>();
                for (int i = 0; i < affectedByWind.Count; ++i)
                {
                    affectedByWind[i].GetComponentsInChildren(gotJointsBuffer);
                    _springBones.AddRange(gotJointsBuffer);
                }
            }
            else
            {
                vrmRoot.GetComponentsInChildren(_springBones);
            }

            if (_springBones.Count == 0)
            {
                Debug.LogWarning($"For VRMWind, no VRM10SpringBoneJoints were found in {vrmRoot.name}. Aborting.", vrmRoot);
                enabled = false;
                return;
            }

            _originalGravityDirections = new Vector3[_springBones.Count];
            _originalGravityFactors = new float[_springBones.Count];
            for (int i = 0; i < _springBones.Count; ++i)
            {
                _originalGravityDirections[i] = _springBones[i].m_gravityDir;
                _originalGravityFactors[i] = _springBones[i].m_gravityPower;
            }
        }

        /// <summary>
        /// VRMを破棄するとき、もしこのコンポーネントが破棄されない場合は、これを呼び出してリソースを解放します。
        /// </summary>
        public void UnloadVrm()
        {
            _springBones.Clear();
            _originalGravityDirections = new Vector3[]{ };
            _originalGravityFactors = new float[] { };
        }

        private void Start()
        {
            if (loadAutomatic)
            {
                LoadVrm(transform);
            }
        }

        private void Update()
        {
            if (!EnableWind)
            {
                return;
            }

            CreateNewWindItemWhenNeeded();
            DeleteExpiredWindItems();

            Vector3 windDirection = Vector3.zero;
            float windForce = 0;
            for (int i = 0; i < _windItems.Count; i++)
            {
                windDirection += _windItems[i].Orientation;
                windForce += _windItems[i].CurrentFactor;
            }

            for (int i = 0; i < _springBones.Count; i++)
            {
                //NOTE: 力を合成して斜めに力をかけるのが狙い

                _springBoneController.SetJointLevel(_springBones[i].transform, new BlittableJointMutable()
                {
                    gravityDir = (_originalGravityDirections[i] + windDirection).normalized,
                    gravityPower = _originalGravityFactors[i] + windForce,
                });
            }
        }

        /// <summary> 風の影響をリセットし、SpringBoneのGravityに関する設定を初期状態に戻します。 </summary>
        private void DisableWind()
        {
            for (int i = 0; i < _springBones.Count; i++)
            {
                _springBoneController.SetJointLevel(_springBones[i].transform, new BlittableJointMutable()
                {
                    gravityDir = _originalGravityDirections[i],
                    gravityPower = _originalGravityFactors[i],
                });
            }
        }

        /// <summary> 時間をカウントすることで、必要なタイミングでランダムな強さと方向を持ったWindItemを生成します。 </summary>
        private void CreateNewWindItemWhenNeeded()
        {
            _windGenerateCount -= Time.deltaTime;
            if (_windGenerateCount > 0)
            {
                return;
            }
            _windGenerateCount = Random.Range(windIntervalRange.x, windIntervalRange.y) * timeFactor;

            var windOrientation = (
                windBaseOrientation.normalized +
                new Vector3(
                   Random.Range(-windOrientationRandomPower, windOrientationRandomPower),
                   Random.Range(-windOrientationRandomPower, windOrientationRandomPower),
                   Random.Range(-windOrientationRandomPower, windOrientationRandomPower)
                    )).normalized;

            _windItems.Add(new WindItem(
                windOrientation,
                Random.Range(windRiseCountRange.x, windRiseCountRange.y),
                Random.Range(windSitCountRange.x, windSitCountRange.y),
                Random.Range(windStrengthRange.x, windStrengthRange.y) * strengthFactor
            ));
        }

        /// <summary> 個別のWindItemについて、時間の経過状態を更新し、不要なオブジェクトがあれば破棄します。 </summary>
        private void DeleteExpiredWindItems()
        {
            //Removeする可能性があるので逆順にやってます
            for (int i = _windItems.Count - 1; i >= 0; i--)
            {
                var item = _windItems[i];
                item.TimeCount += Time.deltaTime;
                if (item.TimeCount >= item.TotalTime)
                {
                    _windItems.RemoveAt(i);
                }
                else
                {
                    _windItems[i] = item;
                }
            }
        }

    }
}
