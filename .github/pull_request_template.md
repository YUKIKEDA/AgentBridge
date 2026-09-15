## Summary

-

## Related

Closes #<issue-number>

- Design: docs/design.md
- Milestone:

<!--
GitHub に Issue を関連付けるには Closing キーワードが必須。
- 有効: 本文中の単独行 `Closes #12`（推奨）/ `Fixes #12` / `Resolves #12`
- 無効になりやすい: 箇条書きだけ（`- Closes #12`）や URL のみ
PR 作成後、GitHub UI で Linked issues に Issue が出ていることを確認すること。
-->

## Test plan

-

## Verification

- [ ] `./build.ps1` をローカルで実行した（GHA が使えない場合はこれが必須ゲート）

## Risk / Rollback

- Risk:
- Rollback: N/A

## Checklist

- [ ] Conventional Commits 形式のタイトル
- [ ] Related に `Closes #N`（または Fixes / Resolves）があり、GitHub 上で Issue が Linked になっている
- [ ] 設計契約を変える場合は設計 PR が先行、または本 PR がドキュメントのみの例外に該当
- [ ] 1 Issue ≈ 1 PR（M0 まとめ例外を除く）
