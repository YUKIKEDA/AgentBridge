## Summary

-

## Related

- Closes #<issue-number>
- Design: docs/design.md
- Milestone:

<!--
GitHub に Issue を関連付けるには、URL だけでなく必ず Closing キーワードを使うこと。
例: Closes #12 / Fixes #12 / Resolves #12
「Issue: https://github.com/.../issues/12」だけの記載ではサイドバー関連付け・自動クローズがされない。
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
