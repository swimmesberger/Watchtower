// The vocabulary-bound facade of the Elarion contribution kernel — created once here and imported by
// every module. Binding to `AppVocabulary` makes every `when` clause compile-checked against the same
// module catalog the backend enforces.
import { createContributionKit } from '@swimmesberger/elarion-contributions'
import { createRouteGuards } from '@swimmesberger/elarion-contributions/tanstack-router'
import type { AppVocabulary } from './vocabulary'

export const { defineModule, defineExtensionPoint, contribute } = createContributionKit<AppVocabulary>()

export const { redirectUnless } = createRouteGuards<AppVocabulary>()
