/**
 * link.js — 外部リポジトリの公開URL定義
 * ------------------------------------------------------------
 * ビルドデータ（Unity の WebGL 出力、配布用 zip、3Dモデル等）は容量が大きいため
 * 別リポジトリで管理し、このリポジトリはページ（HTML/CSS/JS とサムネイル）だけを持つ。
 *
 * 【重要】HTML の href / src には完全URLを直書きしている。
 *   JS が止まってもリンクが死なないようにするため、このファイルはページから
 *   読み込まない。役割は「正しいURLの唯一の出典」を1箇所に置くこと。
 *   ここを変更したら HTML 側も合わせて置換すること。
 *   （scratchpad の check_links.py が、HTML内のURLとこの定義のズレを検出する）
 */

// ベースとなる GitHub Pages のドメイン
const BASE_DOMAIN = "https://shopon1128.github.io";

// 各リポジトリ名
const RP_GAMES_1  = "/ShoponGames_1";   // ゲームのWebGLビルド、FightInt の配布zip
const RP_APP_1    = "/ShoponApps_1";    // BookLib の配布zip
const RP_OTHERS_1 = "/ShoponOthers_1";  // CLAUDE.md、3Dモデルなどの制作物

/*
 * 組み立て例（HTML に直書きする実際の値）
 *   遊ぶ      : BASE_DOMAIN + RP_GAMES_1  + "/TheMeiro/"
 *               → https://shopon1128.github.io/ShoponGames_1/TheMeiro/
 *   配布zip   : BASE_DOMAIN + RP_APP_1    + "/BookLib/BookLib.zip"
 *   その他    : BASE_DOMAIN + RP_OTHERS_1 + "/Model_pl/pl.fbx"
 */
