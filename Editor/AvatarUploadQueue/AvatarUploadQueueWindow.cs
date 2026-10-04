using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3A.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Zephyrant.EditorTools.AvatarUploadQueue
{
    /// <summary>
    /// キューに積んだアバターを上から順にビルド＆アップロードしてくれるウィンドウだよ〜！
    /// 1体ずつ SDK パネルでポチポチするのダルすぎ問題を解決するやつ✨
    /// </summary>
    public class AvatarUploadQueueWindow : EditorWindow
    {
        enum Status { Pending, Uploading, Done, Failed, Skipped }

        // キュー1件ぶんの情報。EditorWindow にシリアライズしとくから、ドメインリロードしても中身は残るよ
        [Serializable]
        class Entry
        {
            public bool enabled = true;
            public GameObject avatar;
            public Texture2D thumbnail;
            public Status status;
            public string message;
        }

        [SerializeField] List<Entry> _entries = new List<Entry>();
        [SerializeField] bool _continueOnError = true;
        [SerializeField] bool _autoThumbnail = true;
        [SerializeField] bool _autoAgreeCopyright = true;

        ReorderableList _list;
        Vector2 _scroll;
        CancellationTokenSource _cts;
        bool _running;
        string _progress;
        float _progressValue;
        // 進捗イベントは別スレッドから飛んでくるから、再描画したいよ〜って印だけ立てとく
        volatile bool _needsRepaint;

        [MenuItem("Tools/Zephyrant/Avatar Upload Queue")]
        static void Open() => GetWindow<AvatarUploadQueueWindow>("Upload Queue");

        void OnEnable()
        {
            _list = new ReorderableList(_entries, typeof(Entry), true, true, false, true)
            {
                drawHeaderCallback = r => EditorGUI.LabelField(r, $"キュー ({_entries.Count})"),
                elementHeight = EditorGUIUtility.singleLineHeight * 2 + 6,
                drawElementCallback = DrawEntry,
            };
        }

        void DrawEntry(Rect rect, int index, bool active, bool focused)
        {
            var e = _entries[index];
            var line = EditorGUIUtility.singleLineHeight;
            rect.y += 2;

            using (new EditorGUI.DisabledScope(_running))
            {
                e.enabled = EditorGUI.Toggle(new Rect(rect.x, rect.y, 18, line), e.enabled);
                e.avatar = (GameObject)EditorGUI.ObjectField(
                    new Rect(rect.x + 20, rect.y, rect.width - 20 - 150, line), e.avatar, typeof(GameObject), true);
                e.thumbnail = (Texture2D)EditorGUI.ObjectField(
                    new Rect(rect.xMax - 146, rect.y, 146, line), e.thumbnail, typeof(Texture2D), false);
            }

            var bid = GetBlueprintId(e.avatar);
            var info = string.IsNullOrEmpty(bid) ? "新規アップロード" : bid;
            var statusText = $"[{e.status}] {info}" + (string.IsNullOrEmpty(e.message) ? "" : $"  {e.message}");
            var style = new GUIStyle(EditorStyles.miniLabel);
            style.normal.textColor = e.status switch
            {
                Status.Done => new Color(0.3f, 0.75f, 0.3f),
                Status.Failed => new Color(0.9f, 0.3f, 0.3f),
                Status.Uploading => new Color(0.4f, 0.65f, 1f),
                _ => style.normal.textColor,
            };
            EditorGUI.LabelField(new Rect(rect.x + 20, rect.y + line + 2, rect.width - 20, line), statusText, style);
        }

        void OnGUI()
        {
            using (new EditorGUI.DisabledScope(_running))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("選択中を追加")) AddAvatars(Selection.gameObjects);
                if (GUILayout.Button("シーン内を全て追加")) AddAvatars(FindSceneAvatars());
                if (GUILayout.Button("状態リセット"))
                    foreach (var e in _entries) { e.status = Status.Pending; e.message = null; }
                if (GUILayout.Button("クリア")) _entries.Clear();
            }

            using (new EditorGUI.DisabledScope(_running))
            {
                _continueOnError = EditorGUILayout.ToggleLeft("失敗しても次へ進む", _continueOnError);
                _autoThumbnail = EditorGUILayout.ToggleLeft("新規アバターでサムネ未指定なら自動撮影", _autoThumbnail);
                _autoAgreeCopyright = EditorGUILayout.ToggleLeft("著作権同意ダイアログに自動で同意（既存アバターのみ）", _autoAgreeCopyright);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            _list.DoLayoutList();
            EditorGUILayout.EndScrollView();

            HandleDragAndDrop();

            if (_running)
            {
                var r = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(r, _progressValue, _progress ?? "");
                if (GUILayout.Button("キャンセル", GUILayout.Height(28))) _cts?.Cancel();
            }
            else
            {
                var count = _entries.Count(x => x.enabled && x.avatar != null && x.status != Status.Done);
                using (new EditorGUI.DisabledScope(count == 0))
                {
                    if (GUILayout.Button($"アップロード開始 ({count})", GUILayout.Height(28))) _ = RunQueue();
                }
            }
        }

        void HandleDragAndDrop()
        {
            var ev = Event.current;
            if (_running || (ev.type != EventType.DragUpdated && ev.type != EventType.DragPerform)) return;
            if (!position.Contains(GUIUtility.GUIToScreenPoint(ev.mousePosition))) return;
            var gos = DragAndDrop.objectReferences.OfType<GameObject>().ToArray();
            if (gos.Length == 0) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (ev.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                AddAvatars(gos);
            }
            ev.Use();
        }

        static IEnumerable<GameObject> FindSceneAvatars()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var d in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
                    yield return d.gameObject;
            }
        }

        void AddAvatars(IEnumerable<GameObject> gos)
        {
            foreach (var go in gos)
            {
                if (go == null || go.GetComponent<VRCAvatarDescriptor>() == null) continue;
                if (EditorUtility.IsPersistent(go)) continue; // Prefab アセットはダメ！シーン上のやつだけ受け付ける
                if (_entries.Any(x => x.avatar == go)) continue;
                _entries.Add(new Entry { avatar = go });
            }
            Repaint();
        }

        static string GetBlueprintId(GameObject go)
        {
            if (go == null) return null;
            var pm = go.GetComponent<PipelineManager>();
            return pm != null ? pm.blueprintId : null;
        }

        async Task RunQueue()
        {
            // SDK にログインしてて、Builder タブが開いてないと builder が取れないから先にチェック！
            if (!APIUser.IsLoggedIn)
            {
                EditorUtility.DisplayDialog("Upload Queue", "VRChat SDK にログインしてください。", "OK");
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                return;
            }

            if (!VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
            {
                EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
                EditorUtility.DisplayDialog("Upload Queue",
                    "VRChat SDK Control Panel の Builder タブを開いた状態で再度実行してください。", "OK");
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
            builder.OnSdkUploadProgress += OnUploadProgress;
            builder.OnSdkBuildProgress += OnBuildProgress;

            try
            {
                // Done になってるやつは飛ばす。もっかい上げたいときは「状態リセット」してね
                var targets = _entries.Where(x => x.enabled && x.avatar != null && x.status != Status.Done).ToList();
                for (int i = 0; i < targets.Count; i++)
                {
                    var e = targets[i];
                    if (_cts.IsCancellationRequested)
                    {
                        e.status = Status.Skipped;
                        e.message = "キャンセル";
                        continue;
                    }

                    e.status = Status.Uploading;
                    e.message = null;
                    _progress = $"({i + 1}/{targets.Count}) {e.avatar.name}";
                    _progressValue = 0;
                    Repaint();

                    try
                    {
                        await UploadOne(builder, e, _cts.Token);
                        e.status = Status.Done;
                        e.message = DateTime.Now.ToString("HH:mm:ss") + " 完了";
                    }
                    catch (OperationCanceledException)
                    {
                        e.status = Status.Failed;
                        e.message = "キャンセル";
                    }
                    catch (Exception ex)
                    {
                        e.status = Status.Failed;
                        e.message = ex.Message;
                        Debug.LogError($"[UploadQueue] {e.avatar.name}: {ex}");
                        // 失敗で止める設定なら、残りは全部 Skipped にして終わらせる
                        if (!_continueOnError) _cts.Cancel();
                    }
                    Repaint();
                }
            }
            finally
            {
                builder.OnSdkUploadProgress -= OnUploadProgress;
                builder.OnSdkBuildProgress -= OnBuildProgress;
                _running = false;
                _cts.Dispose();
                _cts = null;
                Repaint();
            }

            var done = _entries.Count(x => x.status == Status.Done);
            var failed = _entries.Count(x => x.status == Status.Failed);
            EditorUtility.DisplayDialog("Upload Queue", $"完了: {done}  失敗: {failed}", "OK");
        }

        async Task UploadOne(IVRCSdkAvatarBuilderApi builder, Entry e, CancellationToken ct)
        {
            var go = e.avatar;
            // PipelineManager がないと SDK に怒られるから、なければ付けとく
            var pm = go.GetComponent<PipelineManager>();
            if (pm == null) pm = Undo.AddComponent<PipelineManager>(go);

            builder.SelectAvatar(go);

            string thumbnailPath = e.thumbnail != null
                ? Path.GetFullPath(AssetDatabase.GetAssetPath(e.thumbnail))
                : null;

            VRCAvatar avatar;
            if (string.IsNullOrWhiteSpace(pm.blueprintId))
            {
                // 新規アバター：初回はサムネ必須！指定がなければ自動で撮る
                if (thumbnailPath == null)
                {
                    if (!_autoThumbnail) throw new Exception("新規アップロードにはサムネイルが必要です");
                    thumbnailPath = CaptureThumbnail(go);
                }
                avatar = new VRCAvatar
                {
                    Name = go.name,
                    Description = "",
                    Tags = new List<string>(),
                    ReleaseStatus = "private",
                };
            }
            else
            {
                // 既存アバター：サーバーの名前とか説明をそのまま引き継いで上書きアップロード
                avatar = await VRCApi.GetAvatar(pm.blueprintId, true, ct);
                if (_autoAgreeCopyright) await PreAgreeCopyright(avatar.ID);
            }

            await builder.BuildAndUpload(go, avatar, thumbnailPath, ct);
        }

        // 著作権同意ダイアログが毎回出てキュー止まるのヤバいから、アップロード前に同意を送っとく！
        // VRCCopyrightAgreement.HasAgreement / Agree は internal だからリフレクションで呼んでるよ。
        // 失敗しても何もしないで、SDK のいつものダイアログに任せる（キューは止めない）
        static async Task PreAgreeCopyright(string contentId)
        {
            if (string.IsNullOrWhiteSpace(contentId)) return;
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                var type = typeof(VRCCopyrightAgreement);
                var hasAgreement = type.GetMethod("HasAgreement", flags, null, new[] { typeof(string) }, null);
                var agree = type.GetMethod("Agree", flags, null, new[] { typeof(string) }, null);
                if (hasAgreement == null || agree == null)
                {
                    Debug.LogWarning("[UploadQueue] 著作権同意の自動化に失敗（SDK の API が見つかりません）。ダイアログで同意してください。");
                    return;
                }

                if (await (Task<bool>)hasAgreement.Invoke(null, new object[] { contentId })) return;
                if (!await (Task<bool>)agree.Invoke(null, new object[] { contentId }))
                    Debug.LogWarning($"[UploadQueue] 著作権同意の自動送信に失敗: {contentId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UploadQueue] 著作権同意の自動送信でエラー: {ex.Message}");
            }
        }

        // 新規アバター用のサムネを自動で撮るやつ📸 ViewPosition（目の位置）の前にカメラ置いて顔まわりを撮影
        static string CaptureThumbnail(GameObject avatar)
        {
            var desc = avatar.GetComponent<VRCAvatarDescriptor>();
            var t = avatar.transform;
            var head = t.TransformPoint(desc.ViewPosition);
            var camGo = new GameObject("__UploadQueueThumbCam") { hideFlags = HideFlags.HideAndDontSave };
            var rt = new RenderTexture(1200, 900, 24);
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.fieldOfView = 30;
                cam.nearClipPlane = 0.01f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.25f);
                cam.transform.position = head + t.forward * 1.2f;
                cam.transform.LookAt(head - t.up * 0.05f);
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                var path = Path.Combine(Path.GetFullPath("Temp"), $"uploadqueue_{avatar.GetInstanceID()}.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                DestroyImmediate(tex);
                return path;
            }
            finally
            {
                DestroyImmediate(camGo);
                rt.Release();
                DestroyImmediate(rt);
            }
        }

        // 進捗イベントはバックグラウンドスレッドから呼ばれることがあるから、ここで Repaint しちゃダメ！
        // （やるとアップロードごと落ちる💦）OnInspectorUpdate（メインスレッド）でまとめて反映するよ
        void OnUploadProgress(object _, (string status, float percentage) p)
        {
            _progressValue = p.percentage;
            _progress = $"{_progress?.Split('|')[0].Trim()} | {p.status}";
            _needsRepaint = true;
        }

        void OnBuildProgress(object _, string status)
        {
            _progress = $"{_progress?.Split('|')[0].Trim()} | {status}";
            _needsRepaint = true;
        }

        void OnInspectorUpdate()
        {
            if (!_needsRepaint) return;
            _needsRepaint = false;
            Repaint();
        }
    }
}
