# Zephyrant Editor Tools

VRChat アバター制作がちょっとラクになる Unity エディタ拡張まとめだよ〜✨

## 動作環境

- Unity 2022.3
- VRChat SDK - Avatars 3.7.0 以降

## 導入方法

Unity の Package Manager で `+` → `Add package from git URL...` を選んで、これを貼るだけ！

```
https://github.com/Uraki-Zephyrant/unity-editor-tools.git
```

バージョン固定したいときは、末尾に `#v0.1.0` みたいにタグを付けてね。

## ツール一覧

### Avatar Upload Queue

メニュー：`Tools/Zephyrant/Avatar Upload Queue`

キューに入れたアバターを、上から順番にビルド＆アップロードしてくれるやつ！
アバターいっぱいあるときに1体ずつ SDK パネルでポチポチするの、マジでダルいじゃん？それを全部おまかせできるよ🙌

#### できること

- アバターの追加は「選択中を追加」「シーン内を全て追加」、またはドラッグ＆ドロップで OK
- 並び替え・1件ずつの ON/OFF・削除
- アバターごとの状態（Pending / Uploading / Done / Failed）と進捗バーを表示
- 「失敗しても次へ進む」オプションと、キャンセルボタン
- Done になったものは次の実行でスキップ（もう一回上げたいときは「状態リセット」）
- 既存アバター（Blueprint ID あり）は、サーバー上の名前や説明をそのまま引き継いで更新
- 新規アバターは GameObject 名・Private で作成。サムネ未指定なら頭の前から自動で撮影📸
- 既存アバターの著作権同意ダイアログに自動で同意（オプション、初期値 ON）

#### 使い方

1. VRChat SDK Control Panel でログインして、Builder タブを開いとく
2. Upload Queue ウィンドウにアバターを追加
3. 「アップロード開始」を押す！あとは待つだけ〜

#### 注意点

- 新規アバターは ID がアップロード処理の中で決まるから、著作権同意ダイアログが出ちゃう。そこは手動で OK 押してね
- 実行中にスクリプトが再コンパイルされると、キューが止まっちゃうから気をつけて💦
- 著作権同意の自動化は、SDK の internal API をリフレクションで呼んでる。SDK のアップデートで使えなくなったら、いつものダイアログが出るだけでキューは止まらないよ

## License

MIT
